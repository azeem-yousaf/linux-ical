using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ICloudCalendar.Core;
using ICloudCalendar.Infrastructure.Security;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class AppleCalendarEventWriter(
    IAccountCatalog accounts,
    ICredentialVault credentials,
    IAccountSynchronizer synchronizer) : ICalendarEventWriter
{
    public async Task CreateAsync(NewCalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (string.IsNullOrWhiteSpace(calendarEvent.Title)) throw new ArgumentException("An event title is required.");
        if (calendarEvent.EndsAt <= calendarEvent.StartsAt) throw new ArgumentException("The event must end after it starts.");

        var calendar = (await accounts.GetAllCalendarsAsync(cancellationToken))
            .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Id, calendarEvent.CalendarId) && item.IsEnabled)
            ?? throw new KeyNotFoundException("The selected calendar does not exist.");
        var account = await accounts.GetAccountAsync(calendar.AccountId, cancellationToken)
            ?? throw new KeyNotFoundException("The calendar account does not exist.");
        var password = await credentials.RetrieveAsync(account.Id, cancellationToken);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("The iCloud credential is unavailable.");

        var uid = $"{Guid.NewGuid():N}@linux-icloud-calendar";
        var target = new Uri(calendar.RemoteUri, Uri.EscapeDataString(uid) + ".ics");
        using var client = CreateClient(account.UserName, password);
        using var request = new HttpRequestMessage(HttpMethod.Put, target)
        {
            Content = new StringContent(BuildCalendar(uid, calendarEvent), Encoding.UTF8, "text/calendar")
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.NoContent and not HttpStatusCode.OK)
        {
            throw new HttpRequestException("iCloud did not accept the new event.", null, response.StatusCode);
        }

        await synchronizer.SyncAsync(account.Id, cancellationToken);
    }

    private static HttpClient CreateClient(string userName, string password)
    {
        var client = new HttpClient(new ICloudSafeRedirectHandler(
            new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) },
            AppleBasicAuthentication.Create(userName, password))) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxICloudCalendar/0.1");
        return client;
    }

    private static string BuildCalendar(string uid, NewCalendarEvent value)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");
        static string Utc(DateTimeOffset date) => date.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        static string Date(DateTimeOffset date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var start = value.IsAllDay ? $"DTSTART;VALUE=DATE:{Date(value.StartsAt)}" : $"DTSTART:{Utc(value.StartsAt)}";
        var end = value.IsAllDay ? $"DTEND;VALUE=DATE:{Date(value.EndsAt)}" : $"DTEND:{Utc(value.EndsAt)}";
        var optional = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(value.Location)) optional.Append("LOCATION:").Append(Escape(value.Location.Trim())).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(value.Description)) optional.Append("DESCRIPTION:").Append(Escape(value.Description.Trim())).Append("\r\n");
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Linux iCloud Calendar//EN\r\nCALSCALE:GREGORIAN\r\nBEGIN:VEVENT\r\n" +
            $"UID:{uid}\r\nDTSTAMP:{Utc(DateTimeOffset.UtcNow)}\r\n{start}\r\n{end}\r\nSUMMARY:{Escape(value.Title.Trim())}\r\n" + optional + "END:VEVENT\r\nEND:VCALENDAR\r\n";
    }
}
