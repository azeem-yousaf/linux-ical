namespace ICloudCalendar.Core;

public sealed class AdaptiveSyncPolicy
{
    public TimeSpan ActiveInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan NextDelay(bool userIsActive, int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);

        var baseline = userIsActive ? ActiveInterval : IdleInterval;
        if (consecutiveFailures == 0)
        {
            return baseline;
        }

        var multiplier = Math.Pow(2, Math.Min(consecutiveFailures, 20));
        return TimeSpan.FromTicks(Math.Min(
            (long)(baseline.Ticks * multiplier),
            MaximumBackoff.Ticks));
    }
}
