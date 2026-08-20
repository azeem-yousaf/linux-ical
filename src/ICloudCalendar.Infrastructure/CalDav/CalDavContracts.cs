using ICloudCalendar.Core;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed record CalDavResponse(int StatusCode, string Content, Uri? EffectiveUri = null);

public interface ICalDavTransport
{
    Task<CalDavResponse> ReportAsync(Uri calendarUri, string requestBody, CancellationToken cancellationToken);
    Task<CalDavResponse> PropFindAsync(
        Uri resourceUri,
        string requestBody,
        int depth,
        CancellationToken cancellationToken);
}

public sealed record DiscoveredCalendar(
    string Id,
    string DisplayName,
    Uri Uri,
    string? Color,
    string? SyncToken);

public interface ICalendarDiscovery
{
    Task<IReadOnlyList<DiscoveredCalendar>> DiscoverAsync(
        Uri serviceUri,
        CancellationToken cancellationToken = default);
}

public interface ICalendarEndpointResolver
{
    Uri Resolve(string calendarId);
}

public interface ICalendarPayloadParser
{
    IReadOnlyList<CalendarEvent> Parse(string calendarId, string remoteId, string etag, string payload);
}

public interface ICalendarProjectionWindow
{
    DateTimeOffset StartsAt { get; }
    DateTimeOffset EndsAt { get; }
}
