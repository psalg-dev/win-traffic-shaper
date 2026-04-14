using ShapeTraffic.Core.Models;

namespace ShapeTraffic.Core.Abstractions;

public interface ITrafficRepository
{
    Task<IReadOnlyList<TrafficLimitRule>> GetRulesAsync(CancellationToken cancellationToken);

    Task UpsertRuleAsync(TrafficLimitRule rule, CancellationToken cancellationToken);

    Task DeleteRuleAsync(string processKey, CancellationToken cancellationToken);

    Task AppendAggregateSampleAsync(TrafficSample sample, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficSample>> GetAggregateSamplesAsync(DateTimeOffset fromInclusive, CancellationToken cancellationToken);

    Task PruneAggregateSamplesAsync(DateTimeOffset beforeExclusive, CancellationToken cancellationToken);
}