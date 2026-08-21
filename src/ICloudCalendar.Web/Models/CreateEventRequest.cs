namespace ICloudCalendar.Web.Models;

public sealed record CreateEventRequest(
    string CalendarId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? Location,
    string? Description);
