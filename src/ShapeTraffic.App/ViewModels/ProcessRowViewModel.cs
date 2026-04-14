using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ShapeTraffic.Core.Models;

namespace ShapeTraffic.App.ViewModels;

public sealed class ProcessRowViewModel : ObservableObject
{
    private bool _downloadLimitInputDirty;
    private string _downloadLimitInput = string.Empty;
    private long? _downloadLimitBytesPerSecond;
    private string _downloadLimitDisplay = "Unlimited";
    private long _downloadBytesPerSecond;
    private string _downloadRateDisplay = "0 B/s";
    private string _executablePath = string.Empty;
    private bool _hasActiveFlows;
    private string _processKey = string.Empty;
    private int _processCount = 1;
    private string _processName = string.Empty;
    private string _processIdentityDisplay = string.Empty;
    private int _processId;
    private long _totalDownloadBytes;
    private string _totalDownloadDisplay = "0 B";
    private long _totalUploadBytes;
    private string _totalUploadDisplay = "0 B";
    private bool _uploadLimitInputDirty;
    private string _uploadLimitInput = string.Empty;
    private long? _uploadLimitBytesPerSecond;
    private string _uploadLimitDisplay = "Unlimited";
    private long _uploadBytesPerSecond;
    private string _uploadRateDisplay = "0 B/s";

    public string ProcessKey
    {
        get => _processKey;
        private set => SetProperty(ref _processKey, value);
    }

    public int ProcessId
    {
        get => _processId;
        private set => SetProperty(ref _processId, value);
    }

    public int ProcessCount
    {
        get => _processCount;
        private set => SetProperty(ref _processCount, value);
    }

    public string ProcessName
    {
        get => _processName;
        private set => SetProperty(ref _processName, value);
    }

    public string ProcessIdentityDisplay
    {
        get => _processIdentityDisplay;
        private set => SetProperty(ref _processIdentityDisplay, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        private set => SetProperty(ref _executablePath, value);
    }

    public string DownloadRateDisplay
    {
        get => _downloadRateDisplay;
        private set => SetProperty(ref _downloadRateDisplay, value);
    }

    public long DownloadBytesPerSecond
    {
        get => _downloadBytesPerSecond;
        private set => SetProperty(ref _downloadBytesPerSecond, value);
    }

    public string UploadRateDisplay
    {
        get => _uploadRateDisplay;
        private set => SetProperty(ref _uploadRateDisplay, value);
    }

    public long UploadBytesPerSecond
    {
        get => _uploadBytesPerSecond;
        private set => SetProperty(ref _uploadBytesPerSecond, value);
    }

    public string TotalDownloadDisplay
    {
        get => _totalDownloadDisplay;
        private set => SetProperty(ref _totalDownloadDisplay, value);
    }

    public long TotalDownloadBytes
    {
        get => _totalDownloadBytes;
        private set => SetProperty(ref _totalDownloadBytes, value);
    }

    public string TotalUploadDisplay
    {
        get => _totalUploadDisplay;
        private set => SetProperty(ref _totalUploadDisplay, value);
    }

    public long TotalUploadBytes
    {
        get => _totalUploadBytes;
        private set => SetProperty(ref _totalUploadBytes, value);
    }

    public string DownloadLimitDisplay
    {
        get => _downloadLimitDisplay;
        private set => SetProperty(ref _downloadLimitDisplay, value);
    }

    public long? DownloadLimitBytesPerSecond
    {
        get => _downloadLimitBytesPerSecond;
        private set => SetProperty(ref _downloadLimitBytesPerSecond, value);
    }

    public string UploadLimitDisplay
    {
        get => _uploadLimitDisplay;
        private set => SetProperty(ref _uploadLimitDisplay, value);
    }

    public long? UploadLimitBytesPerSecond
    {
        get => _uploadLimitBytesPerSecond;
        private set => SetProperty(ref _uploadLimitBytesPerSecond, value);
    }

    public bool HasActiveFlows
    {
        get => _hasActiveFlows;
        private set => SetProperty(ref _hasActiveFlows, value);
    }

    public long? CurrentDownloadLimitBytesPerSecond => DownloadLimitBytesPerSecond;

    public long? CurrentUploadLimitBytesPerSecond => UploadLimitBytesPerSecond;

    public string DownloadLimitInput
    {
        get => _downloadLimitInput;
        set
        {
            if (SetProperty(ref _downloadLimitInput, value))
            {
                _downloadLimitInputDirty = true;
            }
        }
    }

    public string UploadLimitInput
    {
        get => _uploadLimitInput;
        set
        {
            if (SetProperty(ref _uploadLimitInput, value))
            {
                _uploadLimitInputDirty = true;
            }
        }
    }

    public void Update(ProcessTrafficSnapshot snapshot)
    {
        ProcessKey = snapshot.ProcessKey;
        ProcessId = snapshot.ProcessId;
        ProcessCount = snapshot.ProcessCount;
        ProcessName = snapshot.ProcessName;
        ProcessIdentityDisplay = FormatProcessIdentity(snapshot.ProcessId, snapshot.ProcessCount);
        ExecutablePath = snapshot.ExecutablePath;
        DownloadBytesPerSecond = snapshot.DownloadBytesPerSecond;
        UploadBytesPerSecond = snapshot.UploadBytesPerSecond;
        TotalDownloadBytes = snapshot.TotalDownloadBytes;
        TotalUploadBytes = snapshot.TotalUploadBytes;
        DownloadLimitBytesPerSecond = snapshot.DownloadLimitBytesPerSecond;
        UploadLimitBytesPerSecond = snapshot.UploadLimitBytesPerSecond;
        DownloadRateDisplay = FormatRate(snapshot.DownloadBytesPerSecond);
        UploadRateDisplay = FormatRate(snapshot.UploadBytesPerSecond);
        TotalDownloadDisplay = FormatBytes(snapshot.TotalDownloadBytes);
        TotalUploadDisplay = FormatBytes(snapshot.TotalUploadBytes);
        DownloadLimitDisplay = FormatOptionalRate(snapshot.DownloadLimitBytesPerSecond);
        UploadLimitDisplay = FormatOptionalRate(snapshot.UploadLimitBytesPerSecond);
        HasActiveFlows = snapshot.HasActiveFlows || snapshot.UploadBytesPerSecond > 0 || snapshot.DownloadBytesPerSecond > 0;
        SyncLimitEditorsFromAppliedValues();
    }

    public void CommitAppliedLimits(long? downloadLimitBytesPerSecond, long? uploadLimitBytesPerSecond)
    {
        DownloadLimitBytesPerSecond = downloadLimitBytesPerSecond;
        UploadLimitBytesPerSecond = uploadLimitBytesPerSecond;
        DownloadLimitDisplay = FormatOptionalRate(downloadLimitBytesPerSecond);
        UploadLimitDisplay = FormatOptionalRate(uploadLimitBytesPerSecond);
        _downloadLimitInputDirty = false;
        _uploadLimitInputDirty = false;
        DownloadLimitInput = FormatLimitInput(downloadLimitBytesPerSecond);
        UploadLimitInput = FormatLimitInput(uploadLimitBytesPerSecond);
        _downloadLimitInputDirty = false;
        _uploadLimitInputDirty = false;
    }

    public void ResetLimitEditors()
    {
        _downloadLimitInputDirty = false;
        _uploadLimitInputDirty = false;
        DownloadLimitInput = FormatLimitInput(DownloadLimitBytesPerSecond);
        UploadLimitInput = FormatLimitInput(UploadLimitBytesPerSecond);
        _downloadLimitInputDirty = false;
        _uploadLimitInputDirty = false;
    }

    public static string FormatOptionalRate(long? value)
    {
        return value.HasValue ? FormatRate(value.Value) : "Unlimited";
    }

    public static string FormatRate(long value)
    {
        return $"{FormatBytes(value)}/s";
    }

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = value;
        var index = 0;
        double scaled = size;
        while (scaled >= 1024 && index < units.Length - 1)
        {
            scaled /= 1024;
            index++;
        }

        return $"{scaled:0.##} {units[index]}";
    }

    public static string FormatProcessIdentity(int processId, int processCount)
    {
        return processCount > 1
            ? $"{processCount} processes"
            : $"PID {processId}";
    }

    private void SyncLimitEditorsFromAppliedValues()
    {
        if (!_downloadLimitInputDirty)
        {
            DownloadLimitInput = FormatLimitInput(DownloadLimitBytesPerSecond);
            _downloadLimitInputDirty = false;
        }

        if (!_uploadLimitInputDirty)
        {
            UploadLimitInput = FormatLimitInput(UploadLimitBytesPerSecond);
            _uploadLimitInputDirty = false;
        }
    }

    private static string FormatLimitInput(long? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
        {
            return string.Empty;
        }

        return Math.Round(bytesPerSecond.Value / (1024d * 1024d), 2).ToString("0.##", CultureInfo.CurrentCulture);
    }
}
