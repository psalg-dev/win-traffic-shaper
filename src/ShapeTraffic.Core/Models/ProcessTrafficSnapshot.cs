namespace ShapeTraffic.Core.Models;

public sealed record ProcessTrafficSnapshot(
    string ProcessKey,
    int ProcessId,
    int ProcessCount,
    string ProcessName,
    string ExecutablePath,
    long TotalUploadBytes,
    long TotalDownloadBytes,
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    long? UploadLimitBytesPerSecond,
    long? DownloadLimitBytesPerSecond,
    DateTimeOffset LastSeenAt,
    bool HasActiveFlows)
{
    public long TotalBytes => TotalUploadBytes + TotalDownloadBytes;
}