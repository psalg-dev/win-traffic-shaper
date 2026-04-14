namespace ShapeTraffic.Core.Models;

public sealed record TrafficSample(
    DateTimeOffset Timestamp,
    long UploadBytes,
    long DownloadBytes)
{
    public double UploadMegabitsPerSecond => UploadBytes * 8d / 1_000_000d;

    public double DownloadMegabitsPerSecond => DownloadBytes * 8d / 1_000_000d;
}