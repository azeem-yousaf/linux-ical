namespace ICloudCalendar.Core;

public sealed record CalendarEvent(
    string CalendarId,
    string RemoteId,
    string ETag,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay = false,
    string? Location = null,
    string? Notes = null,
    string? SourceRemoteId = null)
{
    public CalendarEvent Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CalendarId);
        ArgumentException.ThrowIfNullOrWhiteSpace(RemoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ETag);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        if (EndsAt <= StartsAt)
        {
            throw new ArgumentOutOfRangeException(nameof(EndsAt), "An event must end after it starts.");
        }

        return this;
    }
}
