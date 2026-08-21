namespace ICloudCalendar.Web.Models;

public sealed record UpdateEventRequest(
    string CalendarId,
    string ResourceId,
    DateTimeOffset OriginalStartsAt,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? Location,
    string? Description);
