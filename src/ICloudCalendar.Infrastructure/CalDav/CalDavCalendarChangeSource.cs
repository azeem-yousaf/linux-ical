using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ICloudCalendar.Core;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class CalDavCalendarChangeSource(
    ICalDavTransport transport,
    ICalendarEndpointResolver endpoints,
    ICalendarPayloadParser payloadParser) : ICalendarChangeSource
{
    private const int MultiGetBatchSize = 100;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    public async Task<SyncPage> GetChangesAsync(
        string calendarId,
        string? syncToken,
        string? pageCursor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        if (pageCursor is not null)
        {
            throw new ArgumentException("CalDAV sync-collection reports are not cursor paginated.", nameof(pageCursor));
        }

        var response = await transport.ReportAsync(
            endpoints.Resolve(calendarId),
            CreateRequest(syncToken),
            cancellationToken);
        if (syncToken is not null && IndicatesInvalidSyncToken(response))
        {
            throw new SyncTokenRejectedException("The CalDAV server rejected the incremental sync token.");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"CalDAV sync report failed with HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}.",
                null,
                (System.Net.HttpStatusCode)response.StatusCode);
        }

        var document = ParseDocument(response.Content);
        var token = document.Root?.Element(Dav + "sync-token")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CalDavDataException(
                "protocol_sync_token_missing",
                "The CalDAV response did not contain a sync token.");
        }

        var deferredHrefs = new List<string>();
        var changes = document.Descendants(Dav + "response")
            .Select(element => ParseChange(element, calendarId, deferredHrefs))
            .Where(change => change is not null)
            .Cast<CalendarChange>()
            .ToList();

        foreach (var hrefBatch in deferredHrefs.Chunk(MultiGetBatchSize))
        {
            var multiGetResponse = await transport.ReportAsync(
                endpoints.Resolve(calendarId),
                CreateMultiGetRequest(hrefBatch),
                cancellationToken);
            if (multiGetResponse.StatusCode is < 200 or >= 300)
            {
                throw new HttpRequestException(
                    $"CalDAV calendar multiget failed with HTTP {multiGetResponse.StatusCode.ToString(CultureInfo.InvariantCulture)}.",
                    null,
                    (System.Net.HttpStatusCode)multiGetResponse.StatusCode);
            }

            changes.AddRange(ParseDocument(multiGetResponse.Content)
                .Descendants(Dav + "response")
                .Select(element => ParseChange(element, calendarId, null))
                .Where(change => change is not null)
                .Cast<CalendarChange>());
        }

        return new SyncPage(changes, null, token);
    }

    internal static string CreateRequest(string? syncToken)
    {
        var report = new XElement(
            Dav + "sync-collection",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XElement(Dav + "sync-level", "1"),
            new XElement(
                Dav + "prop",
                new XElement(Dav + "getetag"),
                new XElement(CalDav + "calendar-data")));
        if (syncToken is not null)
        {
            report.AddFirst(new XElement(Dav + "sync-token", syncToken));
        }

        return report.ToString(SaveOptions.DisableFormatting);
    }

    internal static string CreateMultiGetRequest(IEnumerable<string> hrefs) => new XElement(
        CalDav + "calendar-multiget",
        new XAttribute(XNamespace.Xmlns + "d", Dav),
        new XAttribute(XNamespace.Xmlns + "c", CalDav),
        new XElement(
            Dav + "prop",
            new XElement(Dav + "getetag"),
            new XElement(CalDav + "calendar-data")),
        hrefs.Select(href => new XElement(Dav + "href", href))).ToString(SaveOptions.DisableFormatting);

    private CalendarChange? ParseChange(
        XElement response,
        string calendarId,
        List<string>? deferredHrefs)
    {
        var href = response.Element(Dav + "href")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new CalDavDataException("protocol_href_missing", "A CalDAV response item did not contain an href.");
        }

        var directStatus = StatusCode(response.Element(Dav + "status")?.Value);
        if (directStatus is 404 or 410)
        {
            return new CalendarChange(href, null);
        }

        var propertyStatuses = response.Elements(Dav + "propstat").ToArray();
        var successfulProperties = propertyStatuses
            .Where(item => StatusCode(item.Element(Dav + "status")?.Value) is >= 200 and < 300)
            .SelectMany(item => item.Element(Dav + "prop")?.Elements() ?? [])
            .GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.First());
        if (successfulProperties.Count == 0)
        {
            return propertyStatuses.Any(item => StatusCode(item.Element(Dav + "status")?.Value) is 404 or 410)
                ? new CalendarChange(href, null)
                : null;
        }

        var etag = successfulProperties.GetValueOrDefault(Dav + "getetag")?.Value.Trim();
        var calendarData = successfulProperties.GetValueOrDefault(CalDav + "calendar-data")?.Value;
        if (string.IsNullOrWhiteSpace(etag) || string.IsNullOrWhiteSpace(calendarData))
        {
            if (href.EndsWith('/'))
            {
                return null;
            }

            if (propertyStatuses.Any(item => StatusCode(item.Element(Dav + "status")?.Value) is 404 or 410))
            {
                return new CalendarChange(href, null);
            }

            if (deferredHrefs is not null)
            {
                deferredHrefs.Add(href);
                return null;
            }

            var errorCode = string.IsNullOrWhiteSpace(etag)
                ? "protocol_etag_missing"
                : "protocol_calendar_data_missing";
            throw new CalDavDataException(
                errorCode,
                "A CalDAV resource did not contain an ETag and calendar data.");
        }

        return new CalendarChange(href, payloadParser.Parse(calendarId, href, etag, calendarData));
    }

    private static int? StatusCode(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static XDocument ParseDocument(string content)
    {
        try
        {
            using var textReader = new StringReader(content);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new CalDavDataException(
                "protocol_xml_invalid",
                "The CalDAV server returned malformed XML.",
                exception);
        }
    }

    private static bool IndicatesInvalidSyncToken(CalDavResponse response)
    {
        if (response.StatusCode is not 403 and not 409 || string.IsNullOrWhiteSpace(response.Content))
        {
            return false;
        }

        try
        {
            return ParseDocument(response.Content)
                .Descendants()
                .Any(item => item.Name == Dav + "valid-sync-token");
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
