using Ical.Net;
using Ical.Net.DataTypes;
using ICloudCalendar.Core;
using IcalCalendarEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class IcalNetCalendarPayloadParser(ICalendarProjectionWindow projectionWindow) : ICalendarPayloadParser
{
    public IReadOnlyList<CalendarEvent> Parse(string calendarId, string remoteId, string etag, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        try
        {
            return ParseCore(calendarId, remoteId, etag, payload);
        }
        catch (CalDavDataException)
        {
            throw;
        }
        catch (FormatException exception)
        {
            throw new CalDavDataException(
                "ical_data_invalid",
                "A CalDAV resource contained unsupported or invalid iCalendar data.",
                exception);
        }
    }

    private CalendarEvent[] ParseCore(string calendarId, string remoteId, string etag, string payload)
    {

        Calendar calendar;
        try
        {
            calendar = Calendar.Load(payload)
                ?? throw new FormatException("The iCalendar payload was empty.");
        }
        catch (Exception exception) when (exception is not FormatException)
        {
            throw new FormatException("The CalDAV resource contained invalid iCalendar data.", exception);
        }

        var source = calendar.Events.FirstOrDefault(item => item.RecurrenceIdentifier is null)
            ?? calendar.Events.FirstOrDefault()
            ?? throw new FormatException("The iCalendar payload did not contain a VEVENT.");
        if (source.DtStart is null)
        {
            throw new FormatException("The VEVENT did not contain DTSTART.");
        }

        if (source.RecurrenceRule is not null
            || source.RecurrenceDates.GetAllDates().Any()
            || source.RecurrenceDates.GetAllPeriods().Any()
            || calendar.Events.Count > 1)
        {
            return ExpandOccurrences(calendar, calendarId, remoteId, etag);
        }

        return [CreateEvent(source, calendarId, remoteId, etag, null)];
    }

    private CalendarEvent[] ExpandOccurrences(
        Calendar calendar,
        string calendarId,
        string remoteId,
        string etag)
    {
        var rangeStart = new CalDateTime(projectionWindow.StartsAt.UtcDateTime, "UTC");
        var rangeEnd = new CalDateTime(projectionWindow.EndsAt.UtcDateTime, "UTC");

        return calendar.GetOccurrences<IcalCalendarEvent>(rangeStart)
            .TakeWhileBefore(rangeEnd)
            .Where(occurrence => occurrence.Source is not IcalCalendarEvent source
                || !StringComparer.OrdinalIgnoreCase.Equals(source.Status, "CANCELLED"))
            .Select(occurrence =>
            {
                var source = occurrence.Source as IcalCalendarEvent
                    ?? throw new FormatException("A VEVENT occurrence had an invalid source.");
                var startTime = occurrence.Period.StartTime
                    ?? throw new FormatException("A VEVENT occurrence did not contain a start time.");
                var endTime = occurrence.Period.EffectiveEndTime
                    ?? throw new FormatException("A VEVENT occurrence did not contain an end time.");
                var startsAt = new DateTimeOffset(startTime.AsUtc, TimeSpan.Zero);
                var endsAt = new DateTimeOffset(endTime.AsUtc, TimeSpan.Zero);
                var identityStart = source.RecurrenceIdentifier is null
                    ? startsAt
                    : new DateTimeOffset(source.RecurrenceIdentifier.StartTime.AsUtc, TimeSpan.Zero);
                var occurrenceId = $"{remoteId}::{identityStart.ToUnixTimeMilliseconds()}";
                return CreateEvent(source, calendarId, occurrenceId, etag, remoteId, startsAt, endsAt);
            })
            .ToArray();
    }

    private static ICloudCalendar.Core.CalendarEvent CreateEvent(
        IcalCalendarEvent source,
        string calendarId,
        string remoteId,
        string etag,
        string? sourceRemoteId,
        DateTimeOffset? occurrenceStart = null,
        DateTimeOffset? occurrenceEnd = null)
    {
        var sourceStart = source.DtStart
            ?? throw new FormatException("The VEVENT did not contain DTSTART.");
        var startsAt = occurrenceStart ?? new DateTimeOffset(sourceStart.AsUtc, TimeSpan.Zero);
        var endsAt = source.DtEnd is not null
            ? occurrenceEnd ?? new DateTimeOffset(source.DtEnd.AsUtc, TimeSpan.Zero)
            : occurrenceEnd ?? startsAt.Add(source.Duration?.ToTimeSpan(sourceStart)
                  ?? (source.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)));
        if (endsAt <= startsAt)
        {
            endsAt = startsAt.Add(source.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(1));
        }

        return new ICloudCalendar.Core.CalendarEvent(
            calendarId,
            remoteId,
            etag,
            string.IsNullOrWhiteSpace(source.Summary) ? "Untitled event" : source.Summary,
            startsAt,
            endsAt,
            source.IsAllDay,
            EmptyToNull(source.Location),
            EmptyToNull(source.Description),
            sourceRemoteId).Validate();
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
