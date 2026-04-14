using ShapeTraffic.Core.Models;

namespace ShapeTraffic.Core.Abstractions;

public interface ITrafficController : IAsyncDisposable
{
    event EventHandler<TrafficOverviewSnapshot>? SnapshotAvailable;

    string DatabasePath { get; }

    bool IsElevated { get; }

    string? StartupError { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessTrafficSnapshot>> GetCurrentProcessesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken);

    Task SaveRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken);

    Task RemoveRuleAsync(string processKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficSample>> GetHistoryAsync(TimeRangeOption range, CancellationToken cancellationToken);
}