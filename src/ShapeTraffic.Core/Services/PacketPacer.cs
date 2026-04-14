using ShapeTraffic.Core.Models;

namespace ShapeTraffic.Core.Services;

public sealed class PacketPacer
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ProcessKey, TrafficDirection Direction), DateTimeOffset> _nextAvailability = new();

    public DateTimeOffset Schedule(string processKey, TrafficDirection direction, int packetBytes, long? limitBytesPerSecond, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(processKey))
        {
            throw new ArgumentException("A process key is required.", nameof(processKey));
        }

        if (packetBytes <= 0)
        {
            return now;
        }

        if (!limitBytesPerSecond.HasValue || limitBytesPerSecond.Value <= 0)
        {
            Reset(processKey, direction);
            return now;
        }

        var key = (processKey, direction);
        var duration = TimeSpan.FromSeconds(packetBytes / (double)limitBytesPerSecond.Value);

        lock (_gate)
        {
            var nextAvailable = _nextAvailability.TryGetValue(key, out var value) ? value : now;
            var scheduledAt = nextAvailable > now ? nextAvailable : now;
            _nextAvailability[key] = scheduledAt + duration;
            return scheduledAt;
        }
    }

    public void Reset(string processKey, TrafficDirection direction)
    {
        lock (_gate)
        {
            _nextAvailability.Remove((processKey, direction));
        }
    }

    public void Reset(string processKey)
    {
        lock (_gate)
        {
            _nextAvailability.Remove((processKey, TrafficDirection.Upload));
            _nextAvailability.Remove((processKey, TrafficDirection.Download));
        }
    }
}