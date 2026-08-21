using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using ICloudCalendar.Core;
using ICloudCalendar.Infrastructure.Security;
using IcalCalendarEvent = Ical.Net.CalendarComponents.CalendarEvent;

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

    public async Task UpdateAsync(UpdatedCalendarEvent calendarEvent, CancellationToken cancellationToken = default)
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

        var target = ResolveEventUri(calendar.RemoteUri, calendarEvent.ResourceId);
        using var client = CreateClient(account.UserName, password);
        using var getResponse = await client.GetAsync(target, cancellationToken);
        if (getResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new KeyNotFoundException("The event no longer exists in iCloud.");
        if (!getResponse.IsSuccessStatusCode)
            throw new HttpRequestException("iCloud could not load the event.", null, getResponse.StatusCode);
        var payload = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var updatedPayload = CalendarEventPayloadEditor.Update(payload, calendarEvent);

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, target)
        {
            Content = new StringContent(updatedPayload, Encoding.UTF8, "text/calendar")
        };
        if (getResponse.Headers.ETag is { } etag) putRequest.Headers.IfMatch.Add(etag);
        using var putResponse = await client.SendAsync(putRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (putResponse.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.NoContent and not HttpStatusCode.OK)
            throw new HttpRequestException("iCloud did not accept the updated event.", null, putResponse.StatusCode);

        await synchronizer.SyncAsync(account.Id, cancellationToken);
    }

    public async Task DeleteAsync(DeletedCalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var calendar = (await accounts.GetAllCalendarsAsync(cancellationToken))
            .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Id, calendarEvent.CalendarId) && item.IsEnabled)
            ?? throw new KeyNotFoundException("The selected calendar does not exist.");
        var account = await accounts.GetAccountAsync(calendar.AccountId, cancellationToken)
            ?? throw new KeyNotFoundException("The calendar account does not exist.");
        var password = await credentials.RetrieveAsync(account.Id, cancellationToken);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("The iCloud credential is unavailable.");

        var target = ResolveEventUri(calendar.RemoteUri, calendarEvent.ResourceId);
        using var client = CreateClient(account.UserName, password);
        using var getResponse = await client.GetAsync(target, cancellationToken);
        if (getResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new KeyNotFoundException("The event no longer exists in iCloud.");
        if (!getResponse.IsSuccessStatusCode)
            throw new HttpRequestException("iCloud could not load the event.", null, getResponse.StatusCode);

        var payload = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var updatedPayload = CalendarEventPayloadEditor.Delete(payload, calendarEvent.OriginalStartsAt);
        using var request = updatedPayload is null
            ? new HttpRequestMessage(HttpMethod.Delete, target)
            : new HttpRequestMessage(HttpMethod.Put, target) { Content = new StringContent(updatedPayload, Encoding.UTF8, "text/calendar") };
        if (getResponse.Headers.ETag is { } etag) request.Headers.IfMatch.Add(etag);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.OK)
            throw new HttpRequestException("iCloud did not accept the deleted event.", null, response.StatusCode);

        await synchronizer.SyncAsync(account.Id, cancellationToken);
    }

    private static Uri ResolveEventUri(Uri calendarUri, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId) || resourceId.Contains("::", StringComparison.Ordinal))
            throw new KeyNotFoundException("The event resource identifier is invalid.");
        var target = new Uri(calendarUri, resourceId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(target.Scheme, Uri.UriSchemeHttps)
            || !StringComparer.OrdinalIgnoreCase.Equals(target.Host, calendarUri.Host)
            || !target.AbsolutePath.StartsWith(calendarUri.AbsolutePath, StringComparison.Ordinal))
            throw new KeyNotFoundException("The event does not belong to the selected calendar.");
        return target;
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

public static class CalendarEventPayloadEditor
{
    public static string? Delete(string payload, DateTimeOffset originalStartsAt)
    {
        var calendar = Ical.Net.Calendar.Load(payload) ?? throw new FormatException("The iCalendar payload was empty.");
        var master = calendar.Events.FirstOrDefault(item => item.RecurrenceIdentifier is null)
            ?? throw new FormatException("The event resource has no editable master event.");
        var recurring = master.RecurrenceRule is not null
            || master.RecurrenceDates.GetAllDates().Any()
            || master.RecurrenceDates.GetAllPeriods().Any()
            || calendar.Events.Any(item => item.RecurrenceIdentifier is not null);
        if (!recurring) return null;

        var recurrenceStart = OriginalRecurrenceStart(master, originalStartsAt);
        var target = calendar.Events.FirstOrDefault(item => RecurrenceMatches(item.RecurrenceIdentifier, recurrenceStart));
        if (target is null)
        {
            target = new IcalCalendarEvent
            {
                Uid = master.Uid,
                RecurrenceIdentifier = new RecurrenceIdentifier(recurrenceStart),
                DtStart = recurrenceStart,
                Summary = master.Summary
            };
            target.End = recurrenceStart.Add(master.EffectiveDuration);
            calendar.Events.Add(target);
        }
        target.Status = "CANCELLED";
        target.Sequence = Math.Max(master.Sequence, target.Sequence) + 1;
        var now = CalDateTime.UtcNow;
        target.DtStamp = now;
        target.LastModified = now;
        return new CalendarSerializer(calendar).SerializeToString()
            ?? throw new FormatException("The deleted occurrence could not be serialized.");
    }

    public static string Update(string payload, UpdatedCalendarEvent value)
    {
        var calendar = Ical.Net.Calendar.Load(payload) ?? throw new FormatException("The iCalendar payload was empty.");
        var master = calendar.Events.FirstOrDefault(item => item.RecurrenceIdentifier is null)
            ?? throw new FormatException("The event resource has no editable master event.");
        var recurring = master.RecurrenceRule is not null
            || master.RecurrenceDates.GetAllDates().Any()
            || master.RecurrenceDates.GetAllPeriods().Any()
            || calendar.Events.Any(item => item.RecurrenceIdentifier is not null);
        IcalCalendarEvent target;
        if (recurring)
        {
            var recurrenceStart = OriginalRecurrenceStart(master, value.OriginalStartsAt);
            target = calendar.Events.FirstOrDefault(item => RecurrenceMatches(item.RecurrenceIdentifier, recurrenceStart))!;
            if (target is null)
            {
                target = new IcalCalendarEvent { Uid = master.Uid, RecurrenceIdentifier = new RecurrenceIdentifier(recurrenceStart) };
                calendar.Events.Add(target);
            }
            target.Sequence = Math.Max(master.Sequence, target.Sequence) + 1;
        }
        else
        {
            target = master;
            target.Sequence++;
        }

        ApplyValues(target, value);
        var now = CalDateTime.UtcNow;
        target.DtStamp = now;
        target.LastModified = now;
        return new CalendarSerializer(calendar).SerializeToString()
            ?? throw new FormatException("The updated event could not be serialized.");
    }

    private static void ApplyValues(IcalCalendarEvent target, UpdatedCalendarEvent value)
    {
        target.Summary = value.Title.Trim();
        target.Location = NullIfEmpty(value.Location);
        target.Description = NullIfEmpty(value.Description);
        target.DtStart = ToCalendarTime(value.StartsAt, value.IsAllDay);
        target.End = ToCalendarTime(value.EndsAt, value.IsAllDay);
    }

    private static CalDateTime OriginalRecurrenceStart(IcalCalendarEvent master, DateTimeOffset original)
    {
        if (master.IsAllDay) return new CalDateTime(DateOnly.FromDateTime(original.UtcDateTime));
        var utc = new CalDateTime(original.UtcDateTime, CalDateTime.UtcTzId);
        return string.IsNullOrWhiteSpace(master.DtStart?.TzId) ? utc : utc.ToTimeZone(master.DtStart.TzId);
    }

    private static bool RecurrenceMatches(RecurrenceIdentifier? identifier, CalDateTime expected)
    {
        if (identifier is null) return false;
        return expected.HasTime
            ? identifier.StartTime.AsUtc == expected.AsUtc
            : DateOnly.FromDateTime(identifier.StartTime.Value) == DateOnly.FromDateTime(expected.Value);
    }

    private static CalDateTime ToCalendarTime(DateTimeOffset value, bool allDay) => allDay
        ? new CalDateTime(DateOnly.FromDateTime(value.Date))
        : new CalDateTime(value.UtcDateTime, CalDateTime.UtcTzId);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
