using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShapeTraffic.App.ViewModels;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Core.Models;
using System.IO;

namespace ShapeTraffic.Core.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void ApplySnapshots_ShouldKeepDirtyLimitInput_WhenSnapshotUpdatesExistingRow()
    {
        using var viewModel = CreateViewModel();
        var initialSnapshot = CreateProcessSnapshot(processKey: "proc-a", downloadBytesPerSecond: 32 * 1024, uploadBytesPerSecond: 4 * 1024);

        ApplySnapshots(viewModel, [initialSnapshot]);

        var row = viewModel.Processes.Should().ContainSingle().Subject;
        row.DownloadLimitInput = "256";
        row.UploadLimitInput = "128";

        ApplySnapshots(viewModel,
        [
            initialSnapshot with
            {
                DownloadBytesPerSecond = 48 * 1024,
                UploadBytesPerSecond = 6 * 1024,
                TotalDownloadBytes = 96 * 1024,
                TotalUploadBytes = 12 * 1024,
            }
        ]);

        viewModel.Processes.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.DownloadLimitInput.Should().Be("256");
        row.UploadLimitInput.Should().Be("128");
    }

    [Fact]
    public void ApplySnapshots_ShouldLiveSortRows_WhenTrafficOrderChanges()
    {
        using var viewModel = CreateViewModel();
        var lowTraffic = CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024);
        var highTraffic = CreateProcessSnapshot(processKey: "proc-b", processName: "beta", downloadBytesPerSecond: 80 * 1024);

        ApplySnapshots(viewModel, [lowTraffic, highTraffic]);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainInOrder("proc-b", "proc-a");

        ApplySnapshots(viewModel,
        [
            lowTraffic with { DownloadBytesPerSecond = 120 * 1024 },
            highTraffic with { DownloadBytesPerSecond = 20 * 1024 },
        ]);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainInOrder("proc-a", "proc-b");
    }

    [Fact]
    public void ApplySnapshots_ShouldDeferRefreshWhileProcessTableEditorIsActive()
    {
        using var viewModel = CreateViewModel();
        var lowTraffic = CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024);
        var highTraffic = CreateProcessSnapshot(processKey: "proc-b", processName: "beta", downloadBytesPerSecond: 80 * 1024);

        ApplySnapshots(viewModel, [lowTraffic, highTraffic]);
        viewModel.SetProcessTableEditorActive(true);

        ApplySnapshots(viewModel,
        [
            lowTraffic with { DownloadBytesPerSecond = 120 * 1024 },
            highTraffic with { DownloadBytesPerSecond = 20 * 1024 },
        ]);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainInOrder("proc-b", "proc-a");

        viewModel.SetProcessTableEditorActive(false);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainInOrder("proc-a", "proc-b");
    }

    [Fact]
    public void ApplySnapshots_ShouldHideProcessesWithoutTraffic_ByDefault()
    {
        using var viewModel = CreateViewModel();

        viewModel.HideProcessesWithoutTraffic.Should().BeTrue();

        ApplySnapshots(viewModel,
        [
            CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024),
            CreateProcessSnapshot(processKey: "proc-b", processName: "beta"),
        ]);

        viewModel.Processes.Should().HaveCount(2);
        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainSingle()
            .Which.Should().Be("proc-a");
    }

    [Fact]
    public void ApplySnapshots_ShouldKeepProcessesWithRatesVisible_WhenSnapshotActiveFlowFlagIsFalse()
    {
        using var viewModel = CreateViewModel();

        ApplySnapshots(viewModel,
        [
            CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024, hasActiveFlows: false),
            CreateProcessSnapshot(processKey: "proc-b", processName: "beta"),
        ]);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainSingle()
            .Which.Should().Be("proc-a");
    }

    [Fact]
    public void HideProcessesWithoutTraffic_ShouldShowIdleProcesses_WhenDisabled()
    {
        using var viewModel = CreateViewModel();

        ApplySnapshots(viewModel,
        [
            CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024),
            CreateProcessSnapshot(processKey: "proc-b", processName: "beta"),
        ]);

        viewModel.HideProcessesWithoutTraffic = false;

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Select(row => row.ProcessKey)
            .Should()
            .ContainInOrder("proc-a", "proc-b");
    }

    [Fact]
    public void ApplySnapshots_ShouldDeferTrafficFilterRefreshWhileProcessTableEditorIsActive()
    {
        using var viewModel = CreateViewModel();
        var snapshot = CreateProcessSnapshot(processKey: "proc-a", processName: "alpha", downloadBytesPerSecond: 40 * 1024);

        ApplySnapshots(viewModel, [snapshot]);
        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Should().ContainSingle();

        viewModel.SetProcessTableEditorActive(true);
        ApplySnapshots(viewModel, [snapshot with { DownloadBytesPerSecond = 0, HasActiveFlows = false }]);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Should().ContainSingle();

        viewModel.SetProcessTableEditorActive(false);

        viewModel.ProcessesView.Cast<ProcessRowViewModel>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.5", 1572864L)]
    [InlineData("1,5", 1572864L)]
    public void ParseLimitMegabytesPerSecond_ShouldAcceptDotAndCommaDecimals(string input, long expectedBytesPerSecond)
    {
        var succeeded = TryParseLimitMegabytesPerSecond(input, out var limitBytesPerSecond, out var error);

        succeeded.Should().BeTrue();
        error.Should().BeEmpty();
        limitBytesPerSecond.Should().Be(expectedBytesPerSecond);
    }

    [Fact]
    public void CommitAppliedLimits_ShouldFormatInputsAsMegabytesPerSecond()
    {
        var row = new ProcessRowViewModel();

        row.CommitAppliedLimits(1572864L, 2621440L);

        NormalizeDecimalSeparator(row.DownloadLimitInput).Should().Be("1.5");
        NormalizeDecimalSeparator(row.UploadLimitInput).Should().Be("2.5");
    }

    [Fact]
    public void Update_ShouldKeepAppliedLimitsVisibleInInputs_WhenEditorIsNotDirty()
    {
        var row = new ProcessRowViewModel();
        var initialSnapshot = CreateProcessSnapshot(
            processKey: "proc-a",
            downloadBytesPerSecond: 32 * 1024,
            uploadBytesPerSecond: 8 * 1024,
            appliedDownloadLimitBytesPerSecond: 1572864L,
            appliedUploadLimitBytesPerSecond: 2621440L);

        row.Update(initialSnapshot);

        NormalizeDecimalSeparator(row.DownloadLimitInput).Should().Be("1.5");
        NormalizeDecimalSeparator(row.UploadLimitInput).Should().Be("2.5");

        row.Update(initialSnapshot with
        {
            DownloadBytesPerSecond = 48 * 1024,
            UploadBytesPerSecond = 12 * 1024,
        });

        NormalizeDecimalSeparator(row.DownloadLimitInput).Should().Be("1.5");
        NormalizeDecimalSeparator(row.UploadLimitInput).Should().Be("2.5");
    }

    [Fact]
    public async Task SaveRowLimitsCommand_ShouldPersistTypedLimits_OnFirstApply()
    {
        var controller = new TestTrafficController();
        using var viewModel = CreateViewModel(controller);
        var snapshot = CreateProcessSnapshot(processKey: "proc-a", processName: "alpha");

        ApplySnapshots(viewModel, [snapshot]);

        var row = viewModel.Processes.Should().ContainSingle().Subject;
        row.DownloadLimitInput = "1.5";
        row.UploadLimitInput = "2.5";

        await viewModel.SaveRowLimitsCommand.ExecuteAsync(row);

        controller.SavedRules.Should().ContainSingle();
        controller.SavedRules[0].DownloadLimitBytesPerSecond.Should().Be(1572864L);
        controller.SavedRules[0].UploadLimitBytesPerSecond.Should().Be(2621440L);
        NormalizeDecimalSeparator(row.DownloadLimitInput).Should().Be("1.5");
        NormalizeDecimalSeparator(row.UploadLimitInput).Should().Be("2.5");
    }

    [Fact]
    public void ApplySnapshots_ShouldShowGroupedProcessCount_WhenSnapshotRepresentsMultipleProcesses()
    {
        using var viewModel = CreateViewModel();
        var groupedSnapshot = CreateProcessSnapshot(processKey: "proc-a", processName: "brave", processId: 1234, processCount: 5, downloadBytesPerSecond: 40 * 1024);

        ApplySnapshots(viewModel, [groupedSnapshot]);

        var row = viewModel.Processes.Should().ContainSingle().Subject;
        row.ProcessIdentityDisplay.Should().Be("5 processes");
    }

    [Fact]
    public void ApplySnapshots_ShouldExposeAggregateTotals_ForHeaderDisplay()
    {
        using var viewModel = CreateViewModel();

        ApplySnapshots(viewModel,
        [
            CreateProcessSnapshot(processKey: "proc-a", totalUploadBytes: 2 * 1024, totalDownloadBytes: 5 * 1024),
            CreateProcessSnapshot(processKey: "proc-b", totalUploadBytes: 3 * 1024, totalDownloadBytes: 7 * 1024),
        ]);

        viewModel.AggregateUploadTotalDisplay.Should().Be("5 KB");
        viewModel.AggregateDownloadTotalDisplay.Should().Be("12 KB");
    }

    [Fact]
    public async Task SelectedTimeRange_ShouldReloadHistory_ForNewSelection()
    {
        var controller = new TestTrafficController();
        using var viewModel = CreateViewModel(controller);

        controller.HistoryRequests.Should().BeEmpty();

        viewModel.SelectedTimeRange = viewModel.TimeRanges[2];
        await Task.Yield();

        controller.HistoryRequests.Should().ContainSingle().Which.Should().Be(TimeRangeOption.LastHour);
    }

    [Fact]
    public void TimeRangeChoice_ToString_ShouldReturnLabel()
    {
        var choice = new TimeRangeChoice(TimeRangeOption.Last15Minutes, "Last 15 minutes");

        choice.ToString().Should().Be("Last 15 minutes");
    }

    private static MainViewModel CreateViewModel(TestTrafficController? controller = null)
    {
        return new MainViewModel(controller ?? new TestTrafficController(), NullLogger<MainViewModel>.Instance, Path.GetTempPath());
    }

    private static bool TryParseLimitMegabytesPerSecond(string text, out long? limitBytesPerSecond, out string error)
    {
        var method = typeof(MainViewModel).GetMethod("TryParseLimitMegabytesPerSecond", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        object?[] arguments = [text, null, null];
        var succeeded = (bool)method!.Invoke(null, arguments)!;
        limitBytesPerSecond = (long?)arguments[1];
        error = (string?)arguments[2] ?? string.Empty;
        return succeeded;
    }

    private static string NormalizeDecimalSeparator(string text)
    {
        return text.Replace(',', '.');
    }

    private static void ApplySnapshots(MainViewModel viewModel, IReadOnlyList<ProcessTrafficSnapshot> snapshots)
    {
        var method = typeof(MainViewModel).GetMethod("ApplySnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(viewModel, [snapshots]);
    }

    private static ProcessTrafficSnapshot CreateProcessSnapshot(
        string processKey,
        string processName = "process",
        int processId = 100,
        int processCount = 1,
        long uploadBytesPerSecond = 0,
        long downloadBytesPerSecond = 0,
        long totalUploadBytes = 0,
        long totalDownloadBytes = 0,
        long? appliedUploadLimitBytesPerSecond = null,
        long? appliedDownloadLimitBytesPerSecond = null,
        bool? hasActiveFlows = null)
    {
        return new ProcessTrafficSnapshot(
            processKey,
            processId,
            processCount,
            processName,
            $"C:\\Apps\\{processName}.exe",
            totalUploadBytes,
            totalDownloadBytes,
            uploadBytesPerSecond,
            downloadBytesPerSecond,
            appliedUploadLimitBytesPerSecond,
            appliedDownloadLimitBytesPerSecond,
            DateTimeOffset.UtcNow,
                hasActiveFlows ?? (uploadBytesPerSecond > 0 || downloadBytesPerSecond > 0));
    }

    private sealed class TestTrafficController : ITrafficController
    {
        public List<TimeRangeOption> HistoryRequests { get; } = [];
        public List<TrafficLimitRule> SavedRules { get; } = [];

        public event EventHandler<TrafficOverviewSnapshot>? SnapshotAvailable
        {
            add { }
            remove { }
        }

        public string DatabasePath => Path.Combine(Path.GetTempPath(), "shapetraffic-tests.sqlite");

        public bool IsElevated => false;

        public string? StartupError => null;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProcessTrafficSnapshot>> GetCurrentProcessesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProcessTrafficSnapshot>>([]);
        }

        public Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TrafficLimitRule>>([]);
        }

        public Task SaveRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken)
        {
            SavedRules.Add(rule);
            return Task.CompletedTask;
        }

        public Task RemoveRuleAsync(string processKey, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TrafficSample>> GetHistoryAsync(TimeRangeOption range, CancellationToken cancellationToken)
        {
            HistoryRequests.Add(range);
            return Task.FromResult<IReadOnlyList<TrafficSample>>([]);
        }
    }
}