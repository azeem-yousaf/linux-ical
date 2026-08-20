namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class RollingCalendarProjectionWindow(TimeProvider timeProvider) : ICalendarProjectionWindow
{
    // Keeping a small history supports events spanning the current view; the long
    // future horizon keeps widget reads entirely local between periodic rebuilds.
    public DateTimeOffset StartsAt => timeProvider.GetUtcNow().AddDays(-31);
    public DateTimeOffset EndsAt => timeProvider.GetUtcNow().AddDays(550);
}
