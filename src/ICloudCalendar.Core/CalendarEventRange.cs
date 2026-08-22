namespace ICloudCalendar.Core;

public static class CalendarEventRange
{
    public static bool Overlaps(
        CalendarEvent calendarEvent,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateOnly? requestedDay = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (rangeEnd <= rangeStart)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), "The range end must be after its start.");
        }

        if (!calendarEvent.IsAllDay)
        {
            return calendarEvent.StartsAt < rangeEnd && calendarEvent.EndsAt > rangeStart;
        }

        var firstDate = requestedDay ?? DateOnly.FromDateTime(rangeStart.DateTime);
        var lastDateExclusive = requestedDay?.AddDays(1) ?? DateOnly.FromDateTime(rangeEnd.DateTime);
        if (requestedDay is null && rangeEnd.TimeOfDay != TimeSpan.Zero)
        {
            lastDateExclusive = lastDateExclusive.AddDays(1);
        }

        var eventStart = DateOnly.FromDateTime(calendarEvent.StartsAt.UtcDateTime);
        var eventEnd = DateOnly.FromDateTime(calendarEvent.EndsAt.UtcDateTime);
        return eventStart < lastDateExclusive && eventEnd > firstDate;
    }
}
