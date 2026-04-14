using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using ShapeTraffic.Core.Abstractions;
using ShapeTraffic.Core.Models;
using ShapeTraffic.Core.Services;

namespace ShapeTraffic.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITrafficController _controller;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<string, ProcessRowViewModel> _processesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrafficSample> _historySamples = [];
    private readonly LineSeries _downloadSeries;
    private readonly LineSeries _uploadSeries;

    private string _currentDownloadDisplay = "0 B/s";
    private string _currentUploadDisplay = "0 B/s";
    private string _aggregateDownloadTotalDisplay = "0 B";
    private string _aggregateUploadTotalDisplay = "0 B";
    private string _databasePath = string.Empty;
    private string _engineStatus = "Starting traffic engine...";
    private bool _initialized;
    private bool _hideProcessesWithoutTraffic = true;
    private bool _isProcessTableEditorActive;
    private string _lastRefreshDisplay = "Never";
    private string _logDirectoryPath = string.Empty;
    private ProcessRowViewModel? _selectedProcess;
    private TimeRangeChoice _selectedTimeRange;

    public MainViewModel(ITrafficController controller, ILogger<MainViewModel> logger, string logDirectoryPath)
    {
        _controller = controller;
        _logger = logger;
        _logDirectoryPath = logDirectoryPath;
        _databasePath = controller.DatabasePath;

        Processes = [];
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcess;
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.DownloadBytesPerSecond), ListSortDirection.Descending));
        if (ProcessesView is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.ProcessName));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.ProcessId));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.DownloadBytesPerSecond));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.UploadBytesPerSecond));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.TotalDownloadBytes));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.TotalUploadBytes));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.DownloadLimitBytesPerSecond));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.UploadLimitBytesPerSecond));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.HasActiveFlows));
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRowViewModel.ExecutablePath));
            liveShaping.IsLiveSorting = true;
        }

        TimeRanges =
        [
            new TimeRangeChoice(TimeRangeOption.Last5Minutes, TimeRangeOption.Last5Minutes.ToDisplayName()),
            new TimeRangeChoice(TimeRangeOption.Last15Minutes, TimeRangeOption.Last15Minutes.ToDisplayName()),
            new TimeRangeChoice(TimeRangeOption.LastHour, TimeRangeOption.LastHour.ToDisplayName()),
            new TimeRangeChoice(TimeRangeOption.Last6Hours, TimeRangeOption.Last6Hours.ToDisplayName()),
            new TimeRangeChoice(TimeRangeOption.Last24Hours, TimeRangeOption.Last24Hours.ToDisplayName()),
        ];
        _selectedTimeRange = TimeRanges[0];

        TrafficPlotModel = new PlotModel
        {
            PlotAreaBorderThickness = new OxyThickness(0),
            Background = OxyColor.FromRgb(16, 24, 33),
            TextColor = OxyColor.FromRgb(239, 247, 255),
            PlotMargins = new OxyThickness(54, 10, 14, 34),
        };
        TrafficPlotModel.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm:ss",
            TextColor = OxyColor.FromRgb(158, 180, 200),
            TicklineColor = OxyColor.FromRgb(48, 70, 88),
            AxislineColor = OxyColor.FromRgb(48, 70, 88),
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromAColor(35, OxyColor.FromRgb(83, 117, 143)),
        });
        TrafficPlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Mbps",
            TextColor = OxyColor.FromRgb(158, 180, 200),
            TitleColor = OxyColor.FromRgb(158, 180, 200),
            TicklineColor = OxyColor.FromRgb(48, 70, 88),
            AxislineColor = OxyColor.FromRgb(48, 70, 88),
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromAColor(35, OxyColor.FromRgb(83, 117, 143)),
            Minimum = 0,
        });

        _downloadSeries = new LineSeries { Title = "Download", Color = OxyColor.FromRgb(53, 194, 183), StrokeThickness = 2.6 };
        _uploadSeries = new LineSeries { Title = "Upload", Color = OxyColor.FromRgb(255, 138, 61), StrokeThickness = 2.6 };
        TrafficPlotModel.Series.Add(_downloadSeries);
        TrafficPlotModel.Series.Add(_uploadSeries);

        RefreshCommand = new AsyncRelayCommand(ReloadHistoryAsync);
        SaveRowLimitsCommand = new AsyncRelayCommand<object?>(SaveRowLimitsAsync);
        ClearRowLimitsCommand = new AsyncRelayCommand<object?>(ClearRowLimitsAsync);
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; }

    public ICollectionView ProcessesView { get; }

    public IReadOnlyList<TimeRangeChoice> TimeRanges { get; }

    public PlotModel TrafficPlotModel { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand<object?> SaveRowLimitsCommand { get; }

    public IAsyncRelayCommand<object?> ClearRowLimitsCommand { get; }

    public string EngineStatus
    {
        get => _engineStatus;
        private set => SetProperty(ref _engineStatus, value);
    }

    public string CurrentUploadDisplay
    {
        get => _currentUploadDisplay;
        private set => SetProperty(ref _currentUploadDisplay, value);
    }

    public string CurrentDownloadDisplay
    {
        get => _currentDownloadDisplay;
        private set => SetProperty(ref _currentDownloadDisplay, value);
    }

    public string AggregateUploadTotalDisplay
    {
        get => _aggregateUploadTotalDisplay;
        private set => SetProperty(ref _aggregateUploadTotalDisplay, value);
    }

    public string AggregateDownloadTotalDisplay
    {
        get => _aggregateDownloadTotalDisplay;
        private set => SetProperty(ref _aggregateDownloadTotalDisplay, value);
    }

    public string DatabasePath
    {
        get => _databasePath;
        private set => SetProperty(ref _databasePath, value);
    }

    public string LogDirectoryPath
    {
        get => _logDirectoryPath;
        private set => SetProperty(ref _logDirectoryPath, value);
    }

    public string LastRefreshDisplay
    {
        get => _lastRefreshDisplay;
        private set => SetProperty(ref _lastRefreshDisplay, value);
    }

    public bool HideProcessesWithoutTraffic
    {
        get => _hideProcessesWithoutTraffic;
        set
        {
            if (SetProperty(ref _hideProcessesWithoutTraffic, value))
            {
                ProcessesView.Refresh();
            }
        }
    }

    public TimeRangeChoice SelectedTimeRange
    {
        get => _selectedTimeRange;
        set
        {
            if (SetProperty(ref _selectedTimeRange, value))
            {
                _ = ReloadHistoryAsync();
            }
        }
    }

    public ProcessRowViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _controller.SnapshotAvailable += OnSnapshotAvailable;
        await _controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
        await ReloadHistoryAsync().ConfigureAwait(true);
        var snapshots = await _controller.GetCurrentProcessesAsync(CancellationToken.None).ConfigureAwait(true);
        ApplySnapshots(snapshots);
        EngineStatus = _controller.StartupError ?? (_controller.IsElevated ? "Traffic engine is live." : "Traffic engine is not elevated.");
    }

    public void SetProcessTableEditorActive(bool isActive)
    {
        if (_isProcessTableEditorActive == isActive)
        {
            return;
        }

        _isProcessTableEditorActive = isActive;

        if (!isActive)
        {
            ProcessesView.Refresh();
        }
    }

    public void Dispose()
    {
        _controller.SnapshotAvailable -= OnSnapshotAvailable;
    }

    private async Task SaveRowLimitsAsync(object? parameter)
    {
        var process = TryGetProcessRow(parameter);
        if (process is null)
        {
            return;
        }

        if (!TryParseLimitMegabytesPerSecond(process.DownloadLimitInput, out var downloadLimit, out var downloadError))
        {
            EngineStatus = $"{process.ProcessName}: {downloadError}";
            return;
        }

        if (!TryParseLimitMegabytesPerSecond(process.UploadLimitInput, out var uploadLimit, out var uploadError))
        {
            EngineStatus = $"{process.ProcessName}: {uploadError}";
            return;
        }

        var rule = TrafficLimitRule.Create(process.ProcessKey, process.ProcessName, uploadLimit, downloadLimit);
        await _controller.SaveRuleAsync(rule, CancellationToken.None).ConfigureAwait(true);
        process.CommitAppliedLimits(downloadLimit, uploadLimit);
        EngineStatus = $"Applied limits for {process.ProcessName}.";
    }

    private async Task ClearRowLimitsAsync(object? parameter)
    {
        var process = TryGetProcessRow(parameter);
        if (process is null)
        {
            return;
        }

        await _controller.RemoveRuleAsync(process.ProcessKey, CancellationToken.None).ConfigureAwait(true);
        process.CommitAppliedLimits(null, null);
        EngineStatus = $"Cleared limits for {process.ProcessName}.";
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            var samples = await _controller.GetHistoryAsync(SelectedTimeRange.Value, CancellationToken.None).ConfigureAwait(true);
            _historySamples.Clear();
            _historySamples.AddRange(samples);
            RenderHistory();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to reload aggregate traffic history.");
            EngineStatus = "Unable to refresh the history chart. See logs for details.";
        }
    }

    private void OnSnapshotAvailable(object? sender, TrafficOverviewSnapshot snapshot)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            ApplySnapshots(snapshot.Processes);
            CurrentUploadDisplay = ProcessRowViewModel.FormatRate(snapshot.AggregateUploadBytesPerSecond);
            CurrentDownloadDisplay = ProcessRowViewModel.FormatRate(snapshot.AggregateDownloadBytesPerSecond);
            EngineStatus = snapshot.StatusMessage ?? (_controller.IsElevated ? "Traffic engine is live." : "Traffic engine is not elevated.");
            LastRefreshDisplay = snapshot.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

            _historySamples.Add(new TrafficSample(snapshot.CapturedAt, snapshot.AggregateUploadBytesPerSecond, snapshot.AggregateDownloadBytesPerSecond));
            var cutoff = TimeRangeOption.Last24Hours.GetFromUtc(snapshot.CapturedAt);
            _historySamples.RemoveAll(sample => sample.Timestamp < cutoff);
            RenderHistory();

            await Task.CompletedTask.ConfigureAwait(true);
        });
    }

    private void ApplySnapshots(IReadOnlyList<ProcessTrafficSnapshot> snapshots)
    {
        var activeProcessKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUploadBytes = 0;
        long totalDownloadBytes = 0;

        foreach (var snapshot in snapshots)
        {
            if (!_processesByKey.TryGetValue(snapshot.ProcessKey, out var row))
            {
                row = new ProcessRowViewModel();
                _processesByKey[snapshot.ProcessKey] = row;
                Processes.Add(row);
            }

            row.Update(snapshot);
            activeProcessKeys.Add(snapshot.ProcessKey);
            totalUploadBytes += snapshot.TotalUploadBytes;
            totalDownloadBytes += snapshot.TotalDownloadBytes;
        }

        var removedSelectedProcess = false;
        foreach (var processKey in _processesByKey.Keys.Except(activeProcessKeys, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var row = _processesByKey[processKey];
            if (ReferenceEquals(SelectedProcess, row))
            {
                removedSelectedProcess = true;
            }

            _processesByKey.Remove(processKey);
            Processes.Remove(row);
        }

        if (removedSelectedProcess)
        {
            SelectedProcess = null;
        }

        AggregateUploadTotalDisplay = ProcessRowViewModel.FormatBytes(totalUploadBytes);
        AggregateDownloadTotalDisplay = ProcessRowViewModel.FormatBytes(totalDownloadBytes);

        if (!_isProcessTableEditorActive)
        {
            ProcessesView.Refresh();
        }
    }

    private void RenderHistory()
    {
        var rangeStart = SelectedTimeRange.Value.GetFromUtc(DateTimeOffset.UtcNow);
        _downloadSeries.Points.Clear();
        _uploadSeries.Points.Clear();

        foreach (var sample in _historySamples.Where(sample => sample.Timestamp >= rangeStart).OrderBy(sample => sample.Timestamp))
        {
            var x = DateTimeAxis.ToDouble(sample.Timestamp.LocalDateTime);
            _downloadSeries.Points.Add(new DataPoint(x, sample.DownloadMegabitsPerSecond));
            _uploadSeries.Points.Add(new DataPoint(x, sample.UploadMegabitsPerSecond));
        }

        TrafficPlotModel.InvalidatePlot(true);
    }

    private bool FilterProcess(object item)
    {
        if (!_hideProcessesWithoutTraffic)
        {
            return true;
        }

        return item is ProcessRowViewModel row && row.HasActiveFlows;
    }

    private static bool TryParseLimitMegabytesPerSecond(string text, out long? limitBytesPerSecond, out string error)
    {
        error = string.Empty;
        limitBytesPerSecond = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalizedInput = NormalizeDecimalSeparator(text);
        if (normalizedInput is null ||
            !decimal.TryParse(
                normalizedInput,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            error = "Limits must be numeric values in MB/s, using ',' or '.' for decimals.";
            return false;
        }

        if (parsed <= 0)
        {
            error = "Limits must be greater than zero, or left blank for unlimited.";
            return false;
        }

        limitBytesPerSecond = (long)Math.Round(parsed * 1024m * 1024m, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string? NormalizeDecimalSeparator(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Contains(',') && trimmed.Contains('.')
            ? null
            : trimmed.Replace(',', '.');
    }

    private static ProcessRowViewModel? TryGetProcessRow(object? parameter)
    {
        return parameter as ProcessRowViewModel;
    }
}
