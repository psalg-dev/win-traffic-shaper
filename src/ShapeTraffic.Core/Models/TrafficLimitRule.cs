namespace ShapeTraffic.Core.Models;

public sealed record TrafficLimitRule(
    string ProcessKey,
    string ProcessName,
    long? UploadLimitBytesPerSecond,
    long? DownloadLimitBytesPerSecond,
    bool IsEnabled,
    DateTimeOffset UpdatedAt)
{
    public static TrafficLimitRule Create(string processKey, string processName, long? uploadLimitBytesPerSecond, long? downloadLimitBytesPerSecond)
    {
        return new TrafficLimitRule(
            processKey,
            processName,
            NormalizeLimit(uploadLimitBytesPerSecond),
            NormalizeLimit(downloadLimitBytesPerSecond),
            true,
            DateTimeOffset.UtcNow);
    }

    public bool HasAnyLimit => UploadLimitBytesPerSecond.HasValue || DownloadLimitBytesPerSecond.HasValue;

    public long? GetLimit(TrafficDirection direction)
    {
        return direction == TrafficDirection.Upload ? UploadLimitBytesPerSecond : DownloadLimitBytesPerSecond;
    }

    public TrafficLimitRule WithUpdatedLimit(long? uploadLimitBytesPerSecond, long? downloadLimitBytesPerSecond)
    {
        return this with
        {
            UploadLimitBytesPerSecond = NormalizeLimit(uploadLimitBytesPerSecond),
            DownloadLimitBytesPerSecond = NormalizeLimit(downloadLimitBytesPerSecond),
            UpdatedAt = DateTimeOffset.UtcNow,
            IsEnabled = true,
        };
    }

    private static long? NormalizeLimit(long? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        return value.Value;
    }
}