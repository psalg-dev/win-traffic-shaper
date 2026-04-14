namespace ShapeTraffic.Core.Models;

public sealed record TrafficOverviewSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessTrafficSnapshot> Processes,
    long AggregateUploadBytesPerSecond,
    long AggregateDownloadBytesPerSecond,
    string? StatusMessage);