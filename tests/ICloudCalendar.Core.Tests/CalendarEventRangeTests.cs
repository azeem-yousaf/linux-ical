using ICloudCalendar.Core;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalendarEventRangeTests
{
    [Theory]
    [InlineData(-7)]
    [InlineData(0)]
    [InlineData(10)]
    public void AllDayEventUsesItsCalendarDateAcrossLocalOffsets(int offsetHours)
    {
        var calendarEvent = AllDay(2026, 8, 22);
        var offset = TimeSpan.FromHours(offsetHours);
        var rangeStart = new DateTimeOffset(2026, 8, 22, 0, 0, 0, offset);

        CalendarEventRange.Overlaps(
            calendarEvent,
            rangeStart,
            rangeStart.AddDays(1),
            new DateOnly(2026, 8, 22)).ShouldBeTrue();
        CalendarEventRange.Overlaps(
            calendarEvent,
            rangeStart.AddDays(-1),
            rangeStart,
            new DateOnly(2026, 8, 21)).ShouldBeFalse();
    }

    [Fact]
    public void TimedEventStillUsesInstantOverlap()
    {
        var calendarEvent = AllDay(2026, 8, 22) with
        {
            IsAllDay = false,
            StartsAt = new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 8, 22, 19, 0, 0, TimeSpan.Zero)
        };

        CalendarEventRange.Overlaps(
            calendarEvent,
            new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.FromHours(-7)),
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(-7))).ShouldBeTrue();
    }

    private static CalendarEvent AllDay(int year, int month, int day) => new(
        "work",
        "holiday.ics",
        "etag-1",
        "Holiday",
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).AddDays(1),
        true);
}
