using ICloudCalendar.Infrastructure.CalDav;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class IcalNetCalendarPayloadParserTests
{
    private readonly IcalNetCalendarPayloadParser _parser = new(new FixedProjectionWindow());

    [Fact]
    public void ParsePreservesUtcTimesAndUserFacingFields()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Linux iCloud Calendar//EN
            BEGIN:VEVENT
            UID:design-review
            DTSTAMP:20260820T080000Z
            DTSTART:20260820T090000Z
            DTEND:20260820T094500Z
            SUMMARY:Product design review
            LOCATION:Studio
            DESCRIPTION:Review the new calendar experience
            END:VEVENT
            END:VCALENDAR
            """;

        var result = _parser.Parse("work", "/work/design-review.ics", "\"etag-1\"", payload).Single();

        result.Title.ShouldBe("Product design review");
        result.StartsAt.ShouldBe(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));
        result.EndsAt.ShouldBe(new DateTimeOffset(2026, 8, 20, 9, 45, 0, TimeSpan.Zero));
        result.Location.ShouldBe("Studio");
        result.Notes.ShouldBe("Review the new calendar experience");
        result.IsAllDay.ShouldBeFalse();
    }

    [Fact]
    public void ParseHandlesAllDayEventWithImplicitOneDayDuration()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Linux iCloud Calendar//EN
            BEGIN:VEVENT
            UID:holiday
            DTSTAMP:20260820T080000Z
            DTSTART;VALUE=DATE:20260824
            SUMMARY:Bank holiday
            END:VEVENT
            END:VCALENDAR
            """;

        var result = _parser.Parse("personal", "/personal/holiday.ics", "\"etag-1\"", payload).Single();

        result.IsAllDay.ShouldBeTrue();
        (result.EndsAt - result.StartsAt).ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void ParseProjectsZeroDurationAppleEventWithMinimumVisibleDuration()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Apple Inc.//macOS//EN
            BEGIN:VEVENT
            UID:reminder
            DTSTAMP:20260820T080000Z
            DTSTART:20260821T100000Z
            DTEND:20260821T100000Z
            SUMMARY:Quick reminder
            END:VEVENT
            END:VCALENDAR
            """;

        var result = _parser.Parse("personal", "/personal/reminder.ics", "\"etag-1\"", payload).Single();

        result.EndsAt.ShouldBe(result.StartsAt.AddMinutes(1));
    }

    [Fact]
    public void ParseRejectsCalendarWithoutEvent()
    {
        const string payload = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR";

        var exception = Should.Throw<CalDavDataException>(
            () => _parser.Parse("work", "/work/empty.ics", "\"etag-1\"", payload));

        exception.ErrorCode.ShouldBe("ical_data_invalid");
        exception.Message.ShouldNotContain("/work/empty.ics");
    }

    [Fact]
    public void ParseExpandsRecurrenceHonorsExceptionAndPreservesLocalTimeAcrossDst()
    {
        const string payload = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Linux iCloud Calendar//EN
            BEGIN:VEVENT
            UID:weekly-planning
            DTSTAMP:20260301T080000Z
            DTSTART;TZID=Europe/London:20260322T090000
            DTEND;TZID=Europe/London:20260322T100000
            RRULE:FREQ=WEEKLY;COUNT=3
            EXDATE;TZID=Europe/London:20260329T090000
            SUMMARY:Weekly planning
            END:VEVENT
            END:VCALENDAR
            """;

        var results = _parser.Parse("work", "/work/weekly.ics", "\"etag-1\"", payload);

        results.Count.ShouldBe(2);
        results.Select(item => item.StartsAt).ShouldBe([
            new DateTimeOffset(2026, 3, 22, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 5, 8, 0, 0, TimeSpan.Zero)
        ]);
        results.ShouldAllBe(item => item.SourceRemoteId == "/work/weekly.ics");
        results.Select(item => item.RemoteId).Distinct().Count().ShouldBe(2);
    }

    private sealed class FixedProjectionWindow : ICalendarProjectionWindow
    {
        public DateTimeOffset StartsAt => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset EndsAt => new(2027, 12, 31, 0, 0, 0, TimeSpan.Zero);
    }
}
