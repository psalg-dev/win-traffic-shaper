using System.Net;
using System.Reflection;
using System.IO;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Core.Models;
using ShapeTraffic.Infrastructure.Services;

namespace ShapeTraffic.Core.Tests;

public sealed class WinDivertTrafficControllerTests
{
    [Fact]
    public void NormalizeAddress_ShouldUseSameKey_ForIpv4AndMappedIpv6()
    {
        var method = typeof(WinDivertTrafficController).GetMethod("NormalizeAddress", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var ipv4 = (string)method!.Invoke(null, [IPAddress.Parse("127.0.0.1")])!;
        var mappedIpv6 = (string)method.Invoke(null, [IPAddress.Parse("::ffff:127.0.0.1")])!;

        ipv4.Should().Be(mappedIpv6);
    }

    [Fact]
    public void CompleteSamplingTick_ShouldScaleRate_ByActualElapsedTime()
    {
        var stateType = typeof(WinDivertTrafficController).GetNestedType("ProcessTrafficState", BindingFlags.NonPublic);
        stateType.Should().NotBeNull();

        var constructor = stateType!.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int), typeof(string), typeof(string), typeof(string)], null);
        constructor.Should().NotBeNull();

        var state = constructor!.Invoke([42, "brave", "key", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"]);
        var sampledAtField = stateType.GetField("_lastSampledAt", BindingFlags.Instance | BindingFlags.NonPublic);
        sampledAtField.Should().NotBeNull();

        var sampleStart = DateTimeOffset.UtcNow;
        sampledAtField!.SetValue(state, sampleStart);

        var recordPacket = stateType.GetMethod("RecordPacket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var completeSamplingTick = stateType.GetMethod("CompleteSamplingTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        recordPacket.Should().NotBeNull();
        completeSamplingTick.Should().NotBeNull();

        recordPacket!.Invoke(state, [TrafficDirection.Download, 10 * 1024 * 1024, sampleStart.AddMilliseconds(500)]);
        var result = ((ValueTuple<long, long>)completeSamplingTick!.Invoke(state, [sampleStart.AddMilliseconds(500)])!);

        result.Item1.Should().Be(0);
        result.Item2.Should().Be(20 * 1024 * 1024);
    }

    [Fact]
    public void TryResolveFlowOwner_ShouldUseConnectionOwnerResolverFallback()
    {
        var controller = new WinDivertTrafficController(
            new TestTrafficRepository(),
            NullLogger<WinDivertTrafficController>.Instance,
            Path.GetTempPath(),
            new FakeConnectionOwnerResolver(Environment.ProcessId));

        var flowKeyType = typeof(WinDivertTrafficController).GetNestedType("FlowKey", BindingFlags.NonPublic);
        flowKeyType.Should().NotBeNull();

        var flowKey = Activator.CreateInstance(flowKeyType!, [
            NormalizeAddress(IPAddress.Parse("192.168.1.25")),
            (ushort)51515,
            NormalizeAddress(IPAddress.Parse("142.250.185.68")),
            (ushort)443,
            (byte)6,
        ]);

        var method = typeof(WinDivertTrafficController).GetMethod("TryResolveFlowOwner", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        object?[] arguments = [flowKey!, null!];
        var resolved = (bool)method!.Invoke(controller, arguments)!;

        resolved.Should().BeTrue();
        arguments[1].Should().NotBeNull();

        var owner = arguments[1]!;
        var processId = (int)owner.GetType().GetProperty("ProcessId")!.GetValue(owner)!;
        processId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void BuildSnapshots_ShouldAggregateMultipleProcessStates_WithSameProcessKey()
    {
        var controller = new WinDivertTrafficController(
            new TestTrafficRepository(),
            NullLogger<WinDivertTrafficController>.Instance,
            Path.GetTempPath(),
            new FakeConnectionOwnerResolver(Environment.ProcessId));

        var stateType = typeof(WinDivertTrafficController).GetNestedType("ProcessTrafficState", BindingFlags.NonPublic);
        stateType.Should().NotBeNull();

        var constructor = stateType!.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int), typeof(string), typeof(string), typeof(string)], null);
        constructor.Should().NotBeNull();

        var first = constructor!.Invoke([2001, "brave", @"c:\program files\bravesoftware\brave-browser\application\brave.exe", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"]);
        var second = constructor.Invoke([2002, "brave", @"c:\program files\bravesoftware\brave-browser\application\brave.exe", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"]);

        var sampleStart = DateTimeOffset.UtcNow;
        SetLastSampledAt(stateType, first, sampleStart);
        SetLastSampledAt(stateType, second, sampleStart);
        RecordPacket(stateType, first, TrafficDirection.Download, 4 * 1024 * 1024, sampleStart.AddSeconds(1));
        RecordPacket(stateType, second, TrafficDirection.Download, 6 * 1024 * 1024, sampleStart.AddSeconds(1));
        CompleteSamplingTick(stateType, first, sampleStart.AddSeconds(1));
        CompleteSamplingTick(stateType, second, sampleStart.AddSeconds(1));

        var statesField = typeof(WinDivertTrafficController).GetField("_processStates", BindingFlags.Instance | BindingFlags.NonPublic);
        statesField.Should().NotBeNull();
        var processStates = statesField!.GetValue(controller)!;
        processStates.GetType().GetMethod("TryAdd")!.Invoke(processStates, [2001, first]);
        processStates.GetType().GetMethod("TryAdd")!.Invoke(processStates, [2002, second]);

        var buildSnapshots = typeof(WinDivertTrafficController).GetMethod("BuildSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
        buildSnapshots.Should().NotBeNull();

        var snapshots = (IReadOnlyList<ProcessTrafficSnapshot>)buildSnapshots!.Invoke(controller, [])!;

        snapshots.Should().ContainSingle();
        snapshots[0].ProcessName.Should().Be("brave");
        snapshots[0].ProcessCount.Should().Be(2);
        snapshots[0].DownloadBytesPerSecond.Should().Be(10 * 1024 * 1024);
        snapshots[0].HasActiveFlows.Should().BeTrue();
    }

    private static string NormalizeAddress(IPAddress address)
    {
        var method = typeof(WinDivertTrafficController).GetMethod("NormalizeAddress", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (string)method!.Invoke(null, [address])!;
    }

    private static void SetLastSampledAt(Type stateType, object state, DateTimeOffset sampledAt)
    {
        stateType.GetField("_lastSampledAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, sampledAt);
    }

    private static void RecordPacket(Type stateType, object state, TrafficDirection direction, int bytes, DateTimeOffset seenAt)
    {
        stateType.GetMethod("RecordPacket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(state, [direction, bytes, seenAt]);
    }

    private static void CompleteSamplingTick(Type stateType, object state, DateTimeOffset sampledAt)
    {
        stateType.GetMethod("CompleteSamplingTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(state, [sampledAt]);
    }

    private sealed class FakeConnectionOwnerResolver(int processId) : IConnectionOwnerResolver
    {
        public bool TryResolveOwner(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, byte protocol, out int resolvedProcessId)
        {
            resolvedProcessId = processId;
            return true;
        }
    }

    private sealed class TestTrafficRepository : ITrafficRepository
    {
        public Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TrafficLimitRule>>([]);
        }

        public Task UpsertRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteRuleAsync(string processKey, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AppendAggregateSampleAsync(TrafficSample sample, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TrafficSample>> GetAggregateSamplesAsync(DateTimeOffset fromInclusive, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TrafficSample>>([]);
        }

        public Task PruneAggregateSamplesAsync(DateTimeOffset beforeExclusive, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}