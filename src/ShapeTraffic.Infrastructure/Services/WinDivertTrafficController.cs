using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Core.Models;
using ShapeTraffic.Core.Services;
using ShapeTraffic.Infrastructure.Native;

namespace ShapeTraffic.Infrastructure.Services;

public sealed class WinDivertTrafficController : ITrafficController
{
    private const string UnclassifiedKey = "unclassified";
    private const string UnclassifiedName = "Unclassified traffic";
    private const int PacketBufferSize = 0xFFFF;
    private static readonly TimeSpan UnknownFlowRetryDelay = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan UnknownFlowResolutionTimeout = TimeSpan.FromMilliseconds(150);

    private readonly ITrafficRepository _repository;
    private readonly ILogger<WinDivertTrafficController> _logger;
    private readonly PacketPacer _packetPacer = new();
    private readonly IConnectionOwnerResolver _connectionOwnerResolver;
    private readonly ConcurrentDictionary<FlowKey, FlowOwner> _flowOwners = new();
    private readonly ConcurrentDictionary<int, ProcessTrafficState> _processStates = new();
    private readonly ConcurrentDictionary<string, TrafficLimitRule> _rulesByProcessKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingPacketsGate = new();
    private readonly PriorityQueue<QueuedPacket, long> _pendingPackets = new();
    private readonly string _databasePath;

    private CancellationTokenSource? _controllerCts;
    private Task? _packetLoopTask;
    private Task? _flowLoopTask;
    private Task? _dispatchLoopTask;
    private Task? _samplingLoopTask;
    private IntPtr _packetHandle = WinDivertNative.InvalidHandle;
    private IntPtr _flowHandle = WinDivertNative.InvalidHandle;
    private volatile bool _started;
    private volatile string? _startupError;

    public WinDivertTrafficController(ITrafficRepository repository, ILogger<WinDivertTrafficController> logger, string databasePath, IConnectionOwnerResolver? connectionOwnerResolver = null)
    {
        _repository = repository;
        _logger = logger;
        _databasePath = databasePath;
        _connectionOwnerResolver = connectionOwnerResolver ?? new WindowsConnectionOwnerResolver();
        IsElevated = IsCurrentProcessElevated();
    }

    public event EventHandler<TrafficOverviewSnapshot>? SnapshotAvailable;

    public string DatabasePath => _databasePath;

    public bool IsElevated { get; }

    public string? StartupError => _startupError;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        PrimeProcessList();

        foreach (var rule in await _repository.GetRulesAsync(cancellationToken).ConfigureAwait(false))
        {
            _rulesByProcessKey[rule.ProcessKey] = rule;
        }

        _controllerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _samplingLoopTask = Task.Run(() => SamplingLoopAsync(_controllerCts.Token), _controllerCts.Token);
        _dispatchLoopTask = Task.Run(() => DispatchLoopAsync(_controllerCts.Token), _controllerCts.Token);

        if (!IsElevated)
        {
            _startupError = "The traffic engine requires administrator privileges. Restart the app elevated to enable live monitoring and shaping.";
            _logger.LogWarning("ShapeTraffic started without administrator privileges; live monitoring is disabled.");
            PublishSnapshot(DateTimeOffset.UtcNow);
            return;
        }

        if (!TryOpenHandles())
        {
            PublishSnapshot(DateTimeOffset.UtcNow);
            return;
        }

        _packetLoopTask = Task.Run(() => PacketCaptureLoopAsync(_controllerCts.Token), _controllerCts.Token);
        _flowLoopTask = Task.Run(() => FlowLoopAsync(_controllerCts.Token), _controllerCts.Token);
        PublishSnapshot(DateTimeOffset.UtcNow);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _controllerCts?.Cancel();
        ShutdownHandles();

        var tasks = new[] { _packetLoopTask, _flowLoopTask, _dispatchLoopTask, _samplingLoopTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseHandles();
            _controllerCts?.Dispose();
            _controllerCts = null;
        }
    }

    public Task<IReadOnlyList<ProcessTrafficSnapshot>> GetCurrentProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildSnapshots());
    }

    public Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TrafficLimitRule> result = _rulesByProcessKey.Values.OrderBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(result);
    }

    public async Task SaveRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken)
    {
        if (!rule.HasAnyLimit)
        {
            await RemoveRuleAsync(rule.ProcessKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        _rulesByProcessKey[rule.ProcessKey] = rule;
        _packetPacer.Reset(rule.ProcessKey);
        await _repository.UpsertRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        PublishSnapshot(DateTimeOffset.UtcNow);
    }

    public async Task RemoveRuleAsync(string processKey, CancellationToken cancellationToken)
    {
        _rulesByProcessKey.TryRemove(processKey, out _);
        _packetPacer.Reset(processKey);
        await _repository.DeleteRuleAsync(processKey, cancellationToken).ConfigureAwait(false);
        PublishSnapshot(DateTimeOffset.UtcNow);
    }

    public Task<IReadOnlyList<TrafficSample>> GetHistoryAsync(TimeRangeOption range, CancellationToken cancellationToken)
    {
        var fromInclusive = range.GetFromUtc(DateTimeOffset.UtcNow);
        return _repository.GetAggregateSamplesAsync(fromInclusive, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private bool TryOpenHandles()
    {
        _flowHandle = WinDivertNative.WinDivertOpen("true", WinDivertNative.Layer.Flow, 0, WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
        if (_flowHandle == WinDivertNative.InvalidHandle)
        {
            _startupError = CreateOpenError("flow handle");
            _logger.LogError("{Message}", _startupError);
            CloseHandles();
            return false;
        }

        _packetHandle = WinDivertNative.WinDivertOpen("!impostor and (tcp or udp)", WinDivertNative.Layer.Network, 0, 0);
        if (_packetHandle == WinDivertNative.InvalidHandle)
        {
            _startupError = CreateOpenError("packet handle");
            _logger.LogError("{Message}", _startupError);
            CloseHandles();
            return false;
        }

        WinDivertNative.WinDivertSetParam(_packetHandle, WinDivertNative.Param.QueueLength, 8192);
        WinDivertNative.WinDivertSetParam(_packetHandle, WinDivertNative.Param.QueueSize, 8UL * 1024UL * 1024UL);
        WinDivertNative.WinDivertSetParam(_packetHandle, WinDivertNative.Param.QueueTime, 2000);

        _startupError = null;
        _logger.LogInformation("WinDivert handles opened successfully.");
        return true;
    }

    private async Task PacketCaptureLoopAsync(CancellationToken cancellationToken)
    {
        var packetBuffer = new byte[PacketBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var address = new WinDivertNative.Address();
            if (!WinDivertNative.WinDivertRecv(_packetHandle, packetBuffer, (uint)packetBuffer.Length, out var packetLength, ref address))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == WinDivertNative.ErrorNoData || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("WinDivert packet receive failed with Win32 error {ErrorCode}.", error);
                continue;
            }

            var capturedPacket = new byte[packetLength];
            Buffer.BlockCopy(packetBuffer, 0, capturedPacket, 0, (int)packetLength);
            ProcessCapturedPacket(capturedPacket, address);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task FlowLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var address = new WinDivertNative.Address();
            if (!WinDivertNative.WinDivertRecvEvent(_flowHandle, IntPtr.Zero, 0, out _, ref address))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == WinDivertNative.ErrorNoData || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("WinDivert flow receive failed with Win32 error {ErrorCode}.", error);
                continue;
            }

            HandleFlowEvent(address);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            QueuedPacket? nextPacket = null;

            lock (_pendingPacketsGate)
            {
                if (_pendingPackets.Count > 0 && _pendingPackets.TryPeek(out _, out var dueAtTicks) && dueAtTicks <= DateTimeOffset.UtcNow.UtcTicks)
                {
                    nextPacket = _pendingPackets.Dequeue();
                }
            }

            if (nextPacket is not null)
            {
                DispatchQueuedPacket(nextPacket);
                continue;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SamplingLoopAsync(CancellationToken cancellationToken)
    {
        var tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var sampledAt = DateTimeOffset.UtcNow;
            var aggregateUpload = 0L;
            var aggregateDownload = 0L;

            foreach (var state in _processStates.Values)
            {
                var (uploadDelta, downloadDelta) = state.CompleteSamplingTick(sampledAt);
                aggregateUpload += uploadDelta;
                aggregateDownload += downloadDelta;
            }

            if (aggregateUpload > 0 || aggregateDownload > 0)
            {
                await _repository.AppendAggregateSampleAsync(new TrafficSample(sampledAt, aggregateUpload, aggregateDownload), cancellationToken).ConfigureAwait(false);
            }

            tick++;
            if (tick % 30 == 0)
            {
                PrimeProcessList();
            }

            if (tick % 300 == 0)
            {
                await _repository.PruneAggregateSamplesAsync(sampledAt.AddDays(-30), cancellationToken).ConfigureAwait(false);
            }

            PublishSnapshot(sampledAt);
        }
    }

    private void ProcessCapturedPacket(byte[] packet, WinDivertNative.Address address)
    {
        if (!TryParseFlowKey(packet, address.Outbound, out var flowKey, out var direction))
        {
            SendPacket(packet, ref address);
            return;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        if (!TryResolveFlowOwner(flowKey, out var flowOwner))
        {
            EnqueuePendingPacket(new QueuedPacket(packet, address, flowKey, direction, capturedAt, capturedAt + UnknownFlowRetryDelay));
            return;
        }

        var dueAt = capturedAt;
        var pacingApplied = false;
        if (_rulesByProcessKey.TryGetValue(flowOwner.ProcessKey, out var rule) && rule.IsEnabled)
        {
            dueAt = _packetPacer.Schedule(flowOwner.ProcessKey, direction, packet.Length, rule.GetLimit(direction), dueAt);
            pacingApplied = dueAt > capturedAt;
        }

        if (!pacingApplied)
        {
            SendTrackedPacket(packet, ref address, flowOwner, direction, capturedAt);
            return;
        }

        EnqueuePendingPacket(new QueuedPacket(packet, address, flowKey, direction, capturedAt, dueAt, flowOwner, true));
    }

    private void HandleFlowEvent(WinDivertNative.Address address)
    {
        var localAddress = NormalizeAddress(WinDivertNative.ToIPAddress(address.Data.Flow.LocalAddr));
        var remoteAddress = NormalizeAddress(WinDivertNative.ToIPAddress(address.Data.Flow.RemoteAddr));
        var localPort = WinDivertNative.WinDivertHelperNtohs(address.Data.Flow.LocalPort);
        var remotePort = WinDivertNative.WinDivertHelperNtohs(address.Data.Flow.RemotePort);
        var flowKey = new FlowKey(localAddress, localPort, remoteAddress, remotePort, address.Data.Flow.Protocol);

        var identity = ResolveProcessIdentity((int)address.Data.Flow.ProcessId);
        var owner = new FlowOwner(identity.ProcessId, identity.ProcessName, identity.ProcessKey, identity.ExecutablePath);
        var state = GetOrCreateState(identity.ProcessId, identity.ProcessName, identity.ProcessKey, identity.ExecutablePath);

        if (address.Event == WinDivertNative.Event.FlowEstablished)
        {
            _flowOwners[flowKey] = owner;
            state.FlowOpened(DateTimeOffset.UtcNow);
        }
        else if (address.Event == WinDivertNative.Event.FlowDeleted)
        {
            _flowOwners.TryRemove(flowKey, out _);
            state.FlowClosed(DateTimeOffset.UtcNow);
        }
    }

    private void DispatchQueuedPacket(QueuedPacket queuedPacket)
    {
        var owner = queuedPacket.Owner;
        var now = DateTimeOffset.UtcNow;

        if (owner is null)
        {
            if (!TryResolveFlowOwner(queuedPacket.FlowKey, out owner))
            {
                if (now - queuedPacket.CapturedAt < UnknownFlowResolutionTimeout)
                {
                    EnqueuePendingPacket(queuedPacket with { DueAt = now + UnknownFlowRetryDelay });
                    return;
                }

                owner = CreateUnclassifiedOwner();
            }
        }

        if (!queuedPacket.PacingApplied &&
            _rulesByProcessKey.TryGetValue(owner.ProcessKey, out var rule) &&
            rule.IsEnabled)
        {
            var pacedAt = _packetPacer.Schedule(owner.ProcessKey, queuedPacket.Direction, queuedPacket.Packet.Length, rule.GetLimit(queuedPacket.Direction), now);
            if (pacedAt > now)
            {
                EnqueuePendingPacket(queuedPacket with { DueAt = pacedAt, Owner = owner, PacingApplied = true });
                return;
            }
        }

        var address = queuedPacket.Address;
        SendTrackedPacket(queuedPacket.Packet, ref address, owner, queuedPacket.Direction, now);
    }

    private void EnqueuePendingPacket(QueuedPacket queuedPacket)
    {
        lock (_pendingPacketsGate)
        {
            _pendingPackets.Enqueue(queuedPacket, queuedPacket.DueAt.UtcTicks);
        }
    }

    private ProcessTrafficState GetOrCreateState(int processId, string processName, string processKey, string executablePath)
    {
        return _processStates.AddOrUpdate(
            processId,
            _ => new ProcessTrafficState(processId, processName, processKey, executablePath),
            (_, existing) =>
            {
                existing.RefreshIdentity(processName, processKey, executablePath);
                return existing;
            });
    }

    private bool TryResolveFlowOwner(FlowKey flowKey, out FlowOwner owner)
    {
        if (_flowOwners.TryGetValue(flowKey, out owner))
        {
            return true;
        }

        if (!_connectionOwnerResolver.TryResolveOwner(flowKey.LocalAddress, flowKey.LocalPort, flowKey.RemoteAddress, flowKey.RemotePort, flowKey.Protocol, out var processId) || processId <= 0)
        {
            owner = default!;
            return false;
        }

        var identity = ResolveProcessIdentity(processId);
        owner = new FlowOwner(identity.ProcessId, identity.ProcessName, identity.ProcessKey, identity.ExecutablePath);
        _flowOwners[flowKey] = owner;
        return true;
    }

    private void SendTrackedPacket(byte[] packet, ref WinDivertNative.Address address, FlowOwner flowOwner, TrafficDirection direction, DateTimeOffset seenAt)
    {
        if (!SendPacket(packet, ref address))
        {
            return;
        }

        var state = GetOrCreateState(flowOwner.ProcessId, flowOwner.ProcessName, flowOwner.ProcessKey, flowOwner.ExecutablePath);
        state.RecordPacket(direction, packet.Length, seenAt);
    }

    private bool SendPacket(byte[] packet, ref WinDivertNative.Address address)
    {
        if (_packetHandle == WinDivertNative.InvalidHandle)
        {
            return false;
        }

        if (!WinDivertNative.WinDivertSend(_packetHandle, packet, (uint)packet.Length, out _, ref address))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogDebug("WinDivert packet reinjection failed with Win32 error {ErrorCode}.", error);
            return false;
        }

        return true;
    }

    private IReadOnlyList<ProcessTrafficSnapshot> BuildSnapshots()
    {
        return _processStates.Values
            .GroupBy(state => state.ProcessKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => AggregateSnapshot(group, _rulesByProcessKey.TryGetValue(group.Key, out var rule) ? rule : null))
            .OrderByDescending(static snapshot => snapshot.UploadBytesPerSecond + snapshot.DownloadBytesPerSecond)
            .ThenBy(static snapshot => snapshot.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProcessTrafficSnapshot AggregateSnapshot(IEnumerable<ProcessTrafficState> states, TrafficLimitRule? rule)
    {
        var materialized = states.ToList();
        var primaryState = materialized
            .OrderByDescending(static state => !string.IsNullOrWhiteSpace(state.ExecutablePath))
            .ThenByDescending(static state => state.CurrentUploadRate + state.CurrentDownloadRate)
            .ThenBy(static state => state.ProcessId)
            .First();
        var aggregateUploadRate = materialized.Sum(static state => state.CurrentUploadRate);
        var aggregateDownloadRate = materialized.Sum(static state => state.CurrentDownloadRate);
        var hasVisibleTraffic = materialized.Any(static state => state.HasActiveFlows) || aggregateUploadRate > 0 || aggregateDownloadRate > 0;

        return new ProcessTrafficSnapshot(
            primaryState.ProcessKey,
            primaryState.ProcessId,
            materialized.Count,
            primaryState.ProcessName,
            primaryState.ExecutablePath,
            materialized.Sum(static state => state.TotalUploadBytes),
            materialized.Sum(static state => state.TotalDownloadBytes),
            aggregateUploadRate,
            aggregateDownloadRate,
            rule?.UploadLimitBytesPerSecond,
            rule?.DownloadLimitBytesPerSecond,
            materialized.Max(static state => state.LastSeenAt),
            hasVisibleTraffic);
    }

    private void PublishSnapshot(DateTimeOffset capturedAt)
    {
        var snapshots = BuildSnapshots();
        var overview = new TrafficOverviewSnapshot(
            capturedAt,
            snapshots,
            snapshots.Sum(static item => item.UploadBytesPerSecond),
            snapshots.Sum(static item => item.DownloadBytesPerSecond),
            _startupError);

        SnapshotAvailable?.Invoke(this, overview);
    }

    private void PrimeProcessList()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var identity = ResolveProcessIdentity(process.Id, process);
                GetOrCreateState(identity.ProcessId, identity.ProcessName, identity.ProcessKey, identity.ExecutablePath);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private ProcessIdentity ResolveProcessIdentity(int processId, Process? process = null)
    {
        try
        {
            using var ownedProcess = process is null ? Process.GetProcessById(processId) : null;
            var currentProcess = process ?? ownedProcess!;

            var processName = currentProcess.ProcessName;
            string executablePath;

            try
            {
                executablePath = currentProcess.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                executablePath = string.Empty;
            }

            var processKey = NormalizeProcessKey(executablePath, processName);
            return new ProcessIdentity(processId, processName, processKey, executablePath);
        }
        catch
        {
            var fallbackName = process?.ProcessName ?? $"PID {processId}";
            return new ProcessIdentity(processId, fallbackName, NormalizeProcessKey(string.Empty, fallbackName), string.Empty);
        }
    }

    private static FlowOwner CreateUnclassifiedOwner()
    {
        return new FlowOwner(0, UnclassifiedName, UnclassifiedKey, string.Empty);
    }

    private static bool TryParseFlowKey(ReadOnlySpan<byte> packet, bool outbound, out FlowKey flowKey, out TrafficDirection direction)
    {
        direction = outbound ? TrafficDirection.Upload : TrafficDirection.Download;
        flowKey = default;

        if (packet.Length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        byte protocol;
        IPAddress sourceAddress;
        IPAddress destinationAddress;
        var transportOffset = 0;

        if (version == 4)
        {
            var headerLength = (packet[0] & 0x0F) * 4;
            if (packet.Length < headerLength + 4)
            {
                return false;
            }

            protocol = packet[9];
            sourceAddress = new IPAddress(packet.Slice(12, 4)).MapToIPv6();
            destinationAddress = new IPAddress(packet.Slice(16, 4)).MapToIPv6();
            transportOffset = headerLength;
        }
        else if (version == 6)
        {
            if (packet.Length < 44)
            {
                return false;
            }

            protocol = packet[6];
            sourceAddress = new IPAddress(packet.Slice(8, 16));
            destinationAddress = new IPAddress(packet.Slice(24, 16));
            transportOffset = 40;
        }
        else
        {
            return false;
        }

        if (protocol is not 6 and not 17)
        {
            return false;
        }

        var sourcePort = (ushort)((packet[transportOffset] << 8) | packet[transportOffset + 1]);
        var destinationPort = (ushort)((packet[transportOffset + 2] << 8) | packet[transportOffset + 3]);
        var localAddress = outbound ? sourceAddress : destinationAddress;
        var remoteAddress = outbound ? destinationAddress : sourceAddress;
        var localPort = outbound ? sourcePort : destinationPort;
        var remotePort = outbound ? destinationPort : sourcePort;

        flowKey = new FlowKey(NormalizeAddress(localAddress), localPort, NormalizeAddress(remoteAddress), remotePort, protocol);
        return true;
    }

    private void ShutdownHandles()
    {
        if (_packetHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertShutdown(_packetHandle, WinDivertNative.ShutdownMode.Both);
        }

        if (_flowHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertShutdown(_flowHandle, WinDivertNative.ShutdownMode.Receive);
        }
    }

    private void CloseHandles()
    {
        if (_packetHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertClose(_packetHandle);
            _packetHandle = WinDivertNative.InvalidHandle;
        }

        if (_flowHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertClose(_flowHandle);
            _flowHandle = WinDivertNative.InvalidHandle;
        }
    }

    private string CreateOpenError(string handleName)
    {
        var error = Marshal.GetLastWin32Error();
        return $"Unable to open the WinDivert {handleName} (Win32 error {error}). Live traffic shaping is unavailable.";
    }

    private static string NormalizeAddress(IPAddress address)
    {
        return ConnectionKeyFormatter.FromIpAddress(address);
    }

    private static string NormalizeProcessKey(string executablePath, string processName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return Path.GetFullPath(executablePath).ToLowerInvariant();
        }

        return $"name:{processName.ToLowerInvariant()}";
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record ProcessIdentity(int ProcessId, string ProcessName, string ProcessKey, string ExecutablePath);

    private sealed record FlowOwner(int ProcessId, string ProcessName, string ProcessKey, string ExecutablePath);

    private sealed record QueuedPacket(
        byte[] Packet,
        WinDivertNative.Address Address,
        FlowKey FlowKey,
        TrafficDirection Direction,
        DateTimeOffset CapturedAt,
        DateTimeOffset DueAt,
        FlowOwner? Owner = null,
        bool PacingApplied = false);

    private readonly record struct FlowKey(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort, byte Protocol);

    private sealed class ProcessTrafficState
    {
        private readonly object _gate = new();
        private long _downloadBytes;
        private DateTimeOffset _lastSampledAt;
        private long _lastSampledDownloadBytes;
        private long _lastSampledUploadBytes;
        private DateTimeOffset _lastSeenAt;
        private int _openFlows;
        private long _uploadBytes;

        public ProcessTrafficState(int processId, string processName, string processKey, string executablePath)
        {
            ProcessId = processId;
            ProcessName = processName;
            ProcessKey = processKey;
            ExecutablePath = executablePath;
            _lastSeenAt = DateTimeOffset.UtcNow;
            _lastSampledAt = _lastSeenAt;
        }

        public int ProcessId { get; }

        public string ProcessName { get; private set; }

        public string ProcessKey { get; private set; }

        public string ExecutablePath { get; private set; }

        public long CurrentDownloadRate { get; private set; }

        public long CurrentUploadRate { get; private set; }

        public long TotalDownloadBytes => _downloadBytes;

        public long TotalUploadBytes => _uploadBytes;

        public DateTimeOffset LastSeenAt => _lastSeenAt;

        public bool HasActiveFlows => _openFlows > 0;

        public void RefreshIdentity(string processName, string processKey, string executablePath)
        {
            lock (_gate)
            {
                ProcessName = processName;
                ProcessKey = processKey;
                if (!string.IsNullOrWhiteSpace(executablePath))
                {
                    ExecutablePath = executablePath;
                }
            }
        }

        public void RecordPacket(TrafficDirection direction, int packetBytes, DateTimeOffset seenAt)
        {
            lock (_gate)
            {
                if (direction == TrafficDirection.Upload)
                {
                    _uploadBytes += packetBytes;
                }
                else
                {
                    _downloadBytes += packetBytes;
                }

                _lastSeenAt = seenAt;
            }
        }

        public void FlowOpened(DateTimeOffset seenAt)
        {
            lock (_gate)
            {
                _openFlows++;
                _lastSeenAt = seenAt;
            }
        }

        public void FlowClosed(DateTimeOffset seenAt)
        {
            lock (_gate)
            {
                _openFlows = Math.Max(0, _openFlows - 1);
                _lastSeenAt = seenAt;
            }
        }

        public (long UploadDelta, long DownloadDelta) CompleteSamplingTick(DateTimeOffset sampledAt)
        {
            lock (_gate)
            {
                var elapsedSeconds = Math.Max((sampledAt - _lastSampledAt).TotalSeconds, 0.001d);
                var uploadBytes = _uploadBytes - _lastSampledUploadBytes;
                var downloadBytes = _downloadBytes - _lastSampledDownloadBytes;

                CurrentUploadRate = (long)Math.Round(uploadBytes / elapsedSeconds, MidpointRounding.AwayFromZero);
                CurrentDownloadRate = (long)Math.Round(downloadBytes / elapsedSeconds, MidpointRounding.AwayFromZero);
                _lastSampledUploadBytes = _uploadBytes;
                _lastSampledDownloadBytes = _downloadBytes;
                _lastSampledAt = sampledAt;

                if (CurrentUploadRate > 0 || CurrentDownloadRate > 0)
                {
                    _lastSeenAt = sampledAt;
                }

                return (CurrentUploadRate, CurrentDownloadRate);
            }
        }

        public ProcessTrafficSnapshot CreateSnapshot(TrafficLimitRule? rule)
        {
            lock (_gate)
            {
                return new ProcessTrafficSnapshot(
                    ProcessKey,
                    ProcessId,
                    1,
                    ProcessName,
                    ExecutablePath,
                    _uploadBytes,
                    _downloadBytes,
                    CurrentUploadRate,
                    CurrentDownloadRate,
                    rule?.UploadLimitBytesPerSecond,
                    rule?.DownloadLimitBytesPerSecond,
                    _lastSeenAt,
                    _openFlows > 0);
            }
        }
    }
}
