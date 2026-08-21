using Ical.Net;
using ICloudCalendar.Core;
using ICloudCalendar.Infrastructure.CalDav;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalendarEventPayloadEditorTests
{
    [Fact]
    public void DeletesANonRepeatingEventByRemovingItsResource()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:appointment@example.com
            DTSTART:20260821T090000Z
            DTEND:20260821T100000Z
            SUMMARY:Appointment
            END:VEVENT
            END:VCALENDAR
            """;

        CalendarEventPayloadEditor.Delete(payload, Utc(2026, 8, 21, 9)).ShouldBeNull();
    }

    [Fact]
    public void DeletesOnlyTheSelectedOccurrenceOfARepeatingEvent()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:weekly@example.com
            DTSTART:20260821T090000Z
            DTEND:20260821T100000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Weekly meeting
            END:VEVENT
            END:VCALENDAR
            """;
        var deletedStart = Utc(2026, 8, 28, 9);

        var updated = CalendarEventPayloadEditor.Delete(payload, deletedStart);
        var calendar = Calendar.Load(updated!)!;

        calendar.Events.Count.ShouldBe(2);
        var cancellation = calendar.Events.Single(item => item.RecurrenceIdentifier is not null);
        cancellation.Uid.ShouldBe("weekly@example.com");
        cancellation.Status.ShouldBe("CANCELLED");
        cancellation.RecurrenceIdentifier!.StartTime.AsUtc.ShouldBe(deletedStart.UtcDateTime);
        var projected = new IcalNetCalendarPayloadParser(new FixedProjectionWindow())
            .Parse("work", "weekly.ics", "etag", updated!);
        projected.Count.ShouldBe(1);
        projected.Single().StartsAt.ShouldBe(Utc(2026, 8, 21, 9));
    }

    [Fact]
    public void UpdatesAnExistingEventWithoutChangingItsIdentity()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:appointment@example.com
            DTSTAMP:20260820T120000Z
            DTSTART:20260821T090000Z
            DTEND:20260821T100000Z
            SUMMARY:Original title
            LOCATION:Old room
            DESCRIPTION:Old notes
            END:VEVENT
            END:VCALENDAR
            """;
        var update = new UpdatedCalendarEvent("work", "appointment.ics", Utc(2026, 8, 21, 9),
            "Updated title", Utc(2026, 8, 21, 11), Utc(2026, 8, 21, 12, 30), false, "New room", "New notes");

        var updatedPayload = CalendarEventPayloadEditor.Update(payload, update);
        var calendar = Calendar.Load(updatedPayload)!;

        calendar.Events.Count.ShouldBe(1);
        var edited = calendar.Events.Single();
        edited.Uid.ShouldBe("appointment@example.com");
        edited.Summary.ShouldBe("Updated title");
        edited.Location.ShouldBe("New room");
        edited.Description.ShouldBe("New notes");
        edited.DtStart!.AsUtc.ShouldBe(new DateTime(2026, 8, 21, 11, 0, 0, DateTimeKind.Utc));
        edited.End!.AsUtc.ShouldBe(new DateTime(2026, 8, 21, 12, 30, 0, DateTimeKind.Utc));
        edited.Sequence.ShouldBe(1);
    }

    [Fact]
    public void UpdatesOneOccurrenceOfARepeatingEventAsAnException()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:weekly@example.com
            DTSTAMP:20260820T120000Z
            DTSTART:20260821T090000Z
            DTEND:20260821T100000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Weekly meeting
            END:VEVENT
            END:VCALENDAR
            """;
        var originalOccurrence = Utc(2026, 8, 28, 9);
        var update = new UpdatedCalendarEvent("work", "weekly.ics", originalOccurrence,
            "Moved meeting", Utc(2026, 8, 28, 14), Utc(2026, 8, 28, 15), false);

        var updatedPayload = CalendarEventPayloadEditor.Update(payload, update);
        var calendar = Calendar.Load(updatedPayload)!;

        calendar.Events.Count.ShouldBe(2);
        var master = calendar.Events.Single(item => item.RecurrenceIdentifier is null);
        var exception = calendar.Events.Single(item => item.RecurrenceIdentifier is not null);
        master.Uid.ShouldBe("weekly@example.com");
        master.RecurrenceRule.ShouldNotBeNull();
        exception.Uid.ShouldBe(master.Uid);
        exception.RecurrenceIdentifier!.StartTime.AsUtc.ShouldBe(originalOccurrence.UtcDateTime);
        exception.Summary.ShouldBe("Moved meeting");
        exception.DtStart!.AsUtc.ShouldBe(new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc));
        var projectedException = new IcalNetCalendarPayloadParser(new FixedProjectionWindow())
            .Parse("work", "weekly.ics", "etag", updatedPayload)
            .Single(item => item.Title == "Moved meeting");
        projectedException.RemoteId.ShouldBe($"weekly.ics::{originalOccurrence.ToUnixTimeMilliseconds()}");
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class FixedProjectionWindow : ICalendarProjectionWindow
    {
        public DateTimeOffset StartsAt => Utc(2026, 1, 1, 0);
        public DateTimeOffset EndsAt => Utc(2027, 1, 1, 0);
    }
}
