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
            throw new FormatException("The CalDAV response did not contain a sync token.");
        }

        var changes = document.Descendants(Dav + "response")
            .Select(element => ParseChange(element, calendarId))
            .Where(change => change is not null)
            .Cast<CalendarChange>()
            .ToArray();

        return new SyncPage(changes, null, token);
    }

    internal static string CreateRequest(string? syncToken)
    {
        var report = new XElement(
            Dav + "sync-collection",
            new XAttribute(XNamespace.Xmlns + "d", Dav),
            new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XElement(Dav + "sync-token", syncToken ?? string.Empty),
            new XElement(Dav + "sync-level", "1"),
            new XElement(
                Dav + "prop",
                new XElement(Dav + "getetag"),
                new XElement(CalDav + "calendar-data")));
        return report.ToString(SaveOptions.DisableFormatting);
    }

    private CalendarChange? ParseChange(XElement response, string calendarId)
    {
        var href = response.Element(Dav + "href")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(href))
        {
            throw new FormatException("A CalDAV response item did not contain an href.");
        }

        var directStatus = StatusCode(response.Element(Dav + "status")?.Value);
        if (directStatus is 404 or 410)
        {
            return new CalendarChange(href, null);
        }

        var successfulProperties = response.Elements(Dav + "propstat")
            .FirstOrDefault(item => StatusCode(item.Element(Dav + "status")?.Value) is >= 200 and < 300)
            ?.Element(Dav + "prop");
        if (successfulProperties is null)
        {
            return null;
        }

        var etag = successfulProperties.Element(Dav + "getetag")?.Value.Trim();
        var calendarData = successfulProperties.Element(CalDav + "calendar-data")?.Value;
        if (string.IsNullOrWhiteSpace(etag) || string.IsNullOrWhiteSpace(calendarData))
        {
            throw new FormatException($"The CalDAV resource '{href}' did not contain an ETag and calendar data.");
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
            throw new FormatException("The CalDAV server returned malformed XML.", exception);
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
