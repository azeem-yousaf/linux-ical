namespace ICloudCalendar.Web.Models;

public sealed record DeleteEventRequest(
    string CalendarId,
    string ResourceId,
    DateTimeOffset OriginalStartsAt);
