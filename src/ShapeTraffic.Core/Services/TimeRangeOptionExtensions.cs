using ShapeTraffic.Core.Models;

namespace ShapeTraffic.Core.Services;

public static class TimeRangeOptionExtensions
{
    public static DateTimeOffset GetFromUtc(this TimeRangeOption option, DateTimeOffset now)
    {
        return option switch
        {
            TimeRangeOption.Last5Minutes => now.AddMinutes(-5),
            TimeRangeOption.Last15Minutes => now.AddMinutes(-15),
            TimeRangeOption.LastHour => now.AddHours(-1),
            TimeRangeOption.Last6Hours => now.AddHours(-6),
            TimeRangeOption.Last24Hours => now.AddHours(-24),
            _ => now.AddMinutes(-5),
        };
    }

    public static string ToDisplayName(this TimeRangeOption option)
    {
        return option switch
        {
            TimeRangeOption.Last5Minutes => "Last 5 minutes",
            TimeRangeOption.Last15Minutes => "Last 15 minutes",
            TimeRangeOption.LastHour => "Last hour",
            TimeRangeOption.Last6Hours => "Last 6 hours",
            TimeRangeOption.Last24Hours => "Last 24 hours",
            _ => "Last 5 minutes",
        };
    }
}