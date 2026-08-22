using ICloudCalendar.Core;
using ICloudCalendar.Infrastructure.Persistence;
using ICloudCalendar.Infrastructure.CalDav;
using ICloudCalendar.Infrastructure.Security;
using ICloudCalendar.Web.Models;
using ICloudCalendar.Web.Services;
using System.Net;
using System.Threading.RateLimiting;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var databasePath = builder.Configuration["Calendar:DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    var dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinuxICloudCalendar");
    Directory.CreateDirectory(dataDirectory);
    databasePath = Path.Combine(dataDirectory, "calendar.db");
}

var connectionString = $"Data Source={databasePath};Cache=Shared;Pooling=True";
builder.Services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(connectionString));
builder.Services.AddSingleton<ISqliteDatabaseInitializer, SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SqliteCalendarStore>();
builder.Services.AddSingleton<IAgendaReader>(services => services.GetRequiredService<SqliteCalendarStore>());
builder.Services.AddSingleton<ICalendarStore>(services => services.GetRequiredService<SqliteCalendarStore>());
builder.Services.AddSingleton<SqliteAccountCatalog>();
builder.Services.AddSingleton<IAccountCatalog>(services => services.GetRequiredService<SqliteAccountCatalog>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClock, TimeProviderClock>();
builder.Services.AddSingleton<ICalendarProjectionWindow, RollingCalendarProjectionWindow>();
builder.Services.AddSingleton<ICalendarPayloadParser, IcalNetCalendarPayloadParser>();
builder.Services.AddSingleton<IProjectionMaintenance, SqliteProjectionMaintenance>();
builder.Services.AddSingleton<IRemoteCalendarSyncSessionFactory, HttpRemoteCalendarSyncSessionFactory>();
builder.Services.AddSingleton<ISecretToolRunner, SecretToolProcessRunner>();
builder.Services.AddSingleton<ICredentialVault, SecretToolCredentialVault>();
builder.Services.AddSingleton<IAppleCalendarProbe, HttpAppleCalendarProbe>();
builder.Services.AddSingleton<IAccountDiscoveryRefresher, AccountDiscoveryRefresher>();
builder.Services.AddSingleton<IAccountSynchronizer, AppleAccountSynchronizer>();
builder.Services.AddSingleton<CalendarSyncCoordinator>();
builder.Services.AddSingleton<ICalendarSyncCoordinator>(services => services.GetRequiredService<CalendarSyncCoordinator>());
builder.Services.AddSingleton<ISyncStatusReader>(services => services.GetRequiredService<CalendarSyncCoordinator>());
builder.Services.AddSingleton<AdaptiveSyncPolicy>();
builder.Services.AddSingleton<SyncWakeSignal>();
builder.Services.AddSingleton<ISyncWakeSignal>(services => services.GetRequiredService<SyncWakeSignal>());
builder.Services.AddSingleton<IUserActivityMonitor, UserActivityMonitor>();
builder.Services.AddSingleton<CalendarSyncBackgroundService>();
builder.Services.AddHostedService(services => services.GetRequiredService<CalendarSyncBackgroundService>());
builder.Services.AddSingleton<IAppleCalendarOnboarding, AppleCalendarOnboarding>();
builder.Services.AddSingleton<IAppleAccountManager, AppleAccountManager>();
builder.Services.AddSingleton<ICalendarEventWriter, AppleCalendarEventWriter>();
builder.Services.AddSingleton<ISoftwareUpdateService, SoftwareUpdateService>();
builder.Services.AddRateLimiter(options => options.AddPolicy("onboarding", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })).AddPolicy("event-write", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })).AddPolicy("manual-sync", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })).AddPolicy("address-search", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })));
var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; " +
        "base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        var isCrossSiteBrowserRequest = string.Equals(
            context.Request.Headers["Sec-Fetch-Site"],
            "cross-site",
            StringComparison.OrdinalIgnoreCase);
        if (isCrossSiteBrowserRequest
            || context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next(context);
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/update", async (ISoftwareUpdateService updates, CancellationToken cancellationToken) =>
    Results.Ok(await updates.CheckAsync(cancellationToken)));
app.MapPost("/api/update/install", async (ISoftwareUpdateService updates, CancellationToken cancellationToken) =>
{
    try
    {
        var update = await updates.StartAsync(cancellationToken);
        return Results.Accepted(value: new { updating = true, update.LatestVersion });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireRateLimiting("event-write");
app.MapGet("/api/locations", async (string? query, CancellationToken cancellationToken) =>
{
    var search = query?.Trim();
    if (string.IsNullOrWhiteSpace(search) || search.Length < 3 || search.Length > 200)
    {
        return Results.BadRequest(new { error = "Enter at least three characters to search for an address." });
    }

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxICloudCalendar/1.0 (https://github.com/azeem-yousaf/linux-ical)");
        var url = "https://photon.komoot.io/api/?limit=6&lang=en&q=" + Uri.EscapeDataString(search);
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return Results.Ok(Array.Empty<object>());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var suggestions = document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => BuildLocationSuggestion(feature.GetProperty("properties")))
            .Where(value => value is not null)
            .Cast<LocationSuggestion>()
            .DistinctBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        return Results.Ok(suggestions);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
    {
        return Results.Ok(Array.Empty<object>());
    }
}).RequireRateLimiting("address-search");
app.MapGet("/api/accounts", async (IAccountCatalog accounts, CancellationToken cancellationToken) =>
{
    var calendars = await accounts.GetAllCalendarsAsync(cancellationToken);
    var result = new List<object>();
    foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
    {
        result.Add(new
        {
            account.Id,
            account.UserName,
            calendars = calendars
                .Where(item => StringComparer.Ordinal.Equals(item.AccountId, account.Id))
                .Select(item => new { item.Id, item.DisplayName, item.Color, item.IsEnabled })
        });
    }

    return Results.Ok(result);
});
app.MapPost("/api/accounts/icloud/connect", async (
    ConnectICloudRequest request,
    HttpContext context,
    IAppleCalendarOnboarding onboarding,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.AppSpecificPassword))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["credentials"] = ["Your Apple Account email and app-specific password are required."]
        });
    }

    try
    {
        var profile = await onboarding.ConnectAsync(
            request.UserName,
            request.AppSpecificPassword,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            profile.AccountId,
            calendars = profile.Calendars.Select(item => new { item.Id, item.DisplayName, item.Color }),
            sync = profile.InitialSync
        });
    }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    {
        return Results.Json(
            new
            {
                error = "Apple rejected the sign-in. Confirm the exact Apple Account email and use a newly generated " +
                    "app-specific password. Apple revokes app-specific passwords after the main account password changes."
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (InvalidOperationException)
    {
        return Results.Json(
            new { error = "The Linux Secret Service is unavailable or locked. Unlock your keyring and try again." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("onboarding");
app.MapDelete("/api/accounts/{accountId}", async (
    string accountId,
    IAccountCatalog accounts,
    IAppleAccountManager accountManager,
    CancellationToken cancellationToken) =>
{
    if (await accounts.GetAccountAsync(accountId, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    try
    {
        await accountManager.DisconnectAsync(accountId, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException)
    {
        return Results.Json(
            new { error = "The Linux keyring is unavailable or locked. Unlock it and try again." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("onboarding");
app.MapGet("/api/widget/agenda", async (
    DateTimeOffset? from,
    DateTimeOffset? to,
    DateOnly? day,
    int? limit,
    IAgendaReader agenda,
    IAccountCatalog accounts,
    IUserActivityMonitor activity,
    CancellationToken cancellationToken) =>
{
    activity.RecordActivity();
    var now = DateTimeOffset.UtcNow;
    var rangeStart = from ?? now.AddMinutes(-15);
    var rangeEnd = to ?? now.AddDays(2);
    var agendaLimit = limit ?? 20;
    if (rangeEnd <= rangeStart || rangeEnd - rangeStart > TimeSpan.FromDays(366))
    {
        return Results.BadRequest(new { error = "Choose a calendar range between one instant and 366 days." });
    }

    if (agendaLimit is < 1 or > 500)
    {
        return Results.BadRequest(new { error = "Agenda limit must be between 1 and 500." });
    }

    // All-day values are calendar dates represented at UTC midnight. Widen the
    // projection query before applying semantic date filtering so those dates
    // remain visible in local timezones on either side of UTC.
    var events = await agenda.GetAgendaAsync(
        rangeEnd.AddDays(1),
        rangeStart.AddDays(-1),
        agendaLimit,
        cancellationToken);
    events = events
        .Where(item => CalendarEventRange.Overlaps(item, rangeStart, rangeEnd, day))
        .Take(agendaLimit)
        .ToArray();
    var calendarDetails = (await accounts.GetAllCalendarsAsync(cancellationToken))
        .ToDictionary(item => item.Id, StringComparer.Ordinal);

    return Results.Ok(new
    {
        generatedAt = now,
        rangeStart,
        rangeEnd,
        events = events.Select(item => new
        {
            id = item.RemoteId,
            resourceId = item.SourceRemoteId ?? item.RemoteId,
            originalStartsAt = OriginalEventStart(item),
            calendarId = item.CalendarId,
            calendarName = calendarDetails.GetValueOrDefault(item.CalendarId)?.DisplayName ?? "Calendar",
            color = calendarDetails.GetValueOrDefault(item.CalendarId)?.Color,
            item.Title,
            item.StartsAt,
            item.EndsAt,
            item.IsAllDay,
            item.Location,
            description = item.Notes
        })
    });
});
app.MapGet("/api/sync/status", (ISyncStatusReader status) => Results.Ok(status.GetAll()));
app.MapPost("/api/events", async (
    CreateEventRequest request,
    ICalendarEventWriter writer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CalendarId) || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["event"] = ["Choose a calendar and enter a title."] });
    }
    if (request.EndsAt <= request.StartsAt)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = ["End time must be after start time."] });
    }
    try
    {
        await writer.CreateAsync(new NewCalendarEvent(request.CalendarId, request.Title, request.StartsAt, request.EndsAt, request.IsAllDay, request.Location, request.Description), cancellationToken);
        return Results.Created($"/api/widget/agenda?from={Uri.EscapeDataString(request.StartsAt.ToString("O"))}", new { created = true });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "The selected calendar is no longer available." }); }
    catch (InvalidOperationException) { return Results.Json(new { error = "Unlock your Linux keyring and try again." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    { return Results.Json(new { error = "iCloud rejected the saved credential. Update the app-specific password and try again." }, statusCode: StatusCodes.Status401Unauthorized); }
    catch (HttpRequestException) { return Results.Json(new { error = "iCloud could not create the event. Check your connection and try again." }, statusCode: StatusCodes.Status502BadGateway); }
}).RequireRateLimiting("event-write");
app.MapPut("/api/events", async (
    UpdateEventRequest request,
    ICalendarEventWriter writer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CalendarId) || string.IsNullOrWhiteSpace(request.ResourceId) || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["event"] = ["The event identity, calendar, and title are required."] });
    }
    if (request.EndsAt <= request.StartsAt)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["endsAt"] = ["End time must be after start time."] });
    }
    try
    {
        await writer.UpdateAsync(new UpdatedCalendarEvent(request.CalendarId, request.ResourceId, request.OriginalStartsAt, request.Title, request.StartsAt, request.EndsAt, request.IsAllDay, request.Location, request.Description), cancellationToken);
        return Results.Ok(new { updated = true });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "The event or calendar is no longer available." }); }
    catch (InvalidOperationException) { return Results.Json(new { error = "Unlock your Linux keyring and try again." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    { return Results.Json(new { error = "iCloud rejected the saved credential. Update the app-specific password and try again." }, statusCode: StatusCodes.Status401Unauthorized); }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed)
    { return Results.Conflict(new { error = "This event changed in iCloud while you were editing it. Refresh and try again." }); }
    catch (HttpRequestException) { return Results.Json(new { error = "iCloud could not update the event. Check your connection and try again." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (FormatException) { return Results.UnprocessableEntity(new { error = "This event uses calendar data that cannot be edited safely." }); }
}).RequireRateLimiting("event-write");
app.MapDelete("/api/events", async (
    [FromBody] DeleteEventRequest request,
    ICalendarEventWriter writer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CalendarId) || string.IsNullOrWhiteSpace(request.ResourceId))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["event"] = ["The event identity and calendar are required."] });
    }
    try
    {
        await writer.DeleteAsync(new DeletedCalendarEvent(request.CalendarId, request.ResourceId, request.OriginalStartsAt), cancellationToken);
        return Results.Ok(new { deleted = true });
    }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "The event or calendar is no longer available." }); }
    catch (InvalidOperationException) { return Results.Json(new { error = "Unlock your Linux keyring and try again." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    { return Results.Json(new { error = "iCloud rejected the saved credential. Update the app-specific password and try again." }, statusCode: StatusCodes.Status401Unauthorized); }
    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed)
    { return Results.Conflict(new { error = "This event changed in iCloud while you were deleting it. Refresh and try again." }); }
    catch (HttpRequestException) { return Results.Json(new { error = "iCloud could not delete the event. Check your connection and try again." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (FormatException) { return Results.UnprocessableEntity(new { error = "This event uses calendar data that cannot be deleted safely." }); }
}).RequireRateLimiting("event-write");
app.MapPost("/api/sync", async (
    ICalendarSyncCoordinator coordinator,
    IUserActivityMonitor activity,
    CancellationToken cancellationToken) =>
{
    activity.RecordActivity();
    var succeeded = await coordinator.SyncAllAsync(cancellationToken);
    return Results.Ok(new { succeeded, accounts = coordinator.GetAll() });
}).RequireRateLimiting("manual-sync");

static DateTimeOffset OriginalEventStart(CalendarEvent calendarEvent)
{
    var marker = calendarEvent.RemoteId.LastIndexOf("::", StringComparison.Ordinal);
    return marker >= 0 && long.TryParse(calendarEvent.RemoteId[(marker + 2)..], out var milliseconds)
        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
        : calendarEvent.StartsAt;
}

app.Run();

static string? GetString(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static string? JoinAddressPart(string? number, string? street) =>
    string.IsNullOrWhiteSpace(street) ? null : string.IsNullOrWhiteSpace(number) ? street : $"{number} {street}";

static LocationSuggestion? BuildLocationSuggestion(JsonElement properties)
{
    var name = GetString(properties, "name");
    var street = JoinAddressPart(GetString(properties, "housenumber"), GetString(properties, "street"));
    var locality = GetString(properties, "city") ?? GetString(properties, "district");
    var region = GetString(properties, "state");
    var postcode = GetString(properties, "postcode");
    var country = GetString(properties, "country");
    var primary = !string.IsNullOrWhiteSpace(name) && (string.IsNullOrWhiteSpace(street) || !street.Contains(name, StringComparison.OrdinalIgnoreCase))
        ? name : street ?? name ?? locality;
    if (string.IsNullOrWhiteSpace(primary)) return null;
    var parts = new[] { name, street, locality, region, postcode, country }
        .Where(part => !string.IsNullOrWhiteSpace(part))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var label = string.Join(", ", parts);
    var secondary = string.Join(", ", parts.Where(part => !StringComparer.OrdinalIgnoreCase.Equals(part, primary)));
    return new LocationSuggestion(label, primary, secondary);
}

public partial class Program;
