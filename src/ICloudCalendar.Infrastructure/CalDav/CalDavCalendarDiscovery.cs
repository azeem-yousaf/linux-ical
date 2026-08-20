using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class CalDavCalendarDiscovery(ICalDavTransport transport) : ICalendarDiscovery
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Apple = "http://apple.com/ns/ical/";

    public async Task<IReadOnlyList<DiscoveredCalendar>> DiscoverAsync(
        Uri serviceUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        if (!serviceUri.IsAbsoluteUri || serviceUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The CalDAV service URI must be an absolute HTTPS URI.", nameof(serviceUri));
        }

        var principalResponse = await transport.PropFindAsync(
            serviceUri,
            PropertyRequest(new XElement(Dav + "current-user-principal")),
            0,
            cancellationToken);
        var principalBase = principalResponse.EffectiveUri ?? serviceUri;
        var principal = ResolveHref(
            principalBase,
            RequiredProperty(principalResponse, Dav + "current-user-principal")?.Element(Dav + "href")?.Value,
            "current-user-principal");

        var homeResponse = await transport.PropFindAsync(
            principal,
            PropertyRequest(new XElement(CalDav + "calendar-home-set")),
            0,
            cancellationToken);
        var homeBase = homeResponse.EffectiveUri ?? principal;
        var home = ResolveHref(
            homeBase,
            RequiredProperty(homeResponse, CalDav + "calendar-home-set")?.Element(Dav + "href")?.Value,
            "calendar-home-set");

        var calendarsResponse = await transport.PropFindAsync(
            home,
            PropertyRequest(
                new XElement(Dav + "displayname"),
                new XElement(Dav + "resourcetype"),
                new XElement(CalDav + "supported-calendar-component-set"),
                new XElement(Dav + "sync-token"),
                new XElement(Apple + "calendar-color")),
            1,
            cancellationToken);
        EnsureSuccess(calendarsResponse);
        var calendarBase = calendarsResponse.EffectiveUri ?? home;
        var document = ParseDocument(calendarsResponse.Content);

        return document.Descendants(Dav + "response")
            .Select(item => ParseCalendar(item, calendarBase))
            .Where(item => item is not null)
            .Cast<DiscoveredCalendar>()
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DiscoveredCalendar? ParseCalendar(XElement response, Uri baseUri)
    {
        var properties = SuccessfulProperties(response);
        if (properties is null
            || properties.Element(Dav + "resourcetype")?.Element(CalDav + "calendar") is null)
        {
            return null;
        }

        var components = properties.Element(CalDav + "supported-calendar-component-set")
            ?.Elements(CalDav + "comp")
            .Select(item => item.Attribute("name")?.Value);
        if (components is not null && !components.Contains("VEVENT", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var href = response.Element(Dav + "href")?.Value;
        var uri = ResolveHref(baseUri, href, "calendar href");
        var displayName = properties.Element(Dav + "displayname")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? "Calendar");
        }

        return new DiscoveredCalendar(
            StableId(uri),
            displayName,
            uri,
            EmptyToNull(properties.Element(Apple + "calendar-color")?.Value),
            EmptyToNull(properties.Element(Dav + "sync-token")?.Value));
    }

    private static XElement? RequiredProperty(CalDavResponse response, XName propertyName)
    {
        EnsureSuccess(response);
        var document = ParseDocument(response.Content);
        var properties = document.Descendants(Dav + "response")
            .Select(SuccessfulProperties)
            .FirstOrDefault(item => item?.Element(propertyName) is not null);
        return properties?.Element(propertyName)
            ?? throw new FormatException($"The CalDAV response did not contain {propertyName.LocalName}.");
    }

    private static XElement? SuccessfulProperties(XElement response) => response
        .Elements(Dav + "propstat")
        .FirstOrDefault(item => StatusCode(item.Element(Dav + "status")?.Value) is >= 200 and < 300)
        ?.Element(Dav + "prop");

    private static string PropertyRequest(params XElement[] properties) => new XElement(
        Dav + "propfind",
        new XAttribute(XNamespace.Xmlns + "d", Dav),
        new XAttribute(XNamespace.Xmlns + "c", CalDav),
        new XAttribute(XNamespace.Xmlns + "a", Apple),
        new XElement(Dav + "prop", properties)).ToString(SaveOptions.DisableFormatting);

    private static Uri ResolveHref(Uri baseUri, string? href, string property)
    {
        if (string.IsNullOrWhiteSpace(href)
            || !Uri.TryCreate(baseUri, href.Trim(), out var resolved)
            || resolved.Scheme != Uri.UriSchemeHttps)
        {
            throw new FormatException($"The CalDAV {property} was missing or invalid.");
        }

        return resolved;
    }

    private static void EnsureSuccess(CalDavResponse response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"CalDAV discovery failed with HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}.",
                null,
                (System.Net.HttpStatusCode)response.StatusCode);
        }
    }

    private static int? StatusCode(string? status)
    {
        var parts = status?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 }
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static XDocument ParseDocument(string content)
    {
        try
        {
            using var text = new StringReader(content);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new FormatException("The CalDAV server returned malformed XML.", exception);
        }
    }

    private static string StableId(Uri uri) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant()[..16];

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
