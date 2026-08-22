using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class WebApplicationSmokeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"icloud-web-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task PublishedSurfaceServesUiAndSafeLocalApiBehavior()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Calendar:DatabasePath", _databasePath));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var home = await client.GetAsync("/");
        var homeContent = await home.Content.ReadAsStringAsync();
        var favicon = await client.GetAsync("/favicon-32.png");
        var manifest = await client.GetAsync("/app.webmanifest");
        var manifestContent = await manifest.Content.ReadAsStringAsync();
        var accounts = await client.GetFromJsonAsync<IReadOnlyList<object>>("/api/accounts");
        var sync = await client.PostAsync("/api/sync", null);
        var invalidAgenda = await client.GetAsync("/api/widget/agenda?limit=0");
        var invalidEvent = await client.PostAsJsonAsync("/api/events", new { calendarId = "", title = "" });
        var missingCalendarEvent = await client.PostAsJsonAsync("/api/events", new
        {
            calendarId = "missing",
            title = "Appointment",
            startsAt = DateTimeOffset.UtcNow.AddHours(1),
            endsAt = DateTimeOffset.UtcNow.AddHours(2),
            isAllDay = false
        });
        var invalidEventUpdate = await client.PutAsJsonAsync("/api/events", new { calendarId = "", resourceId = "", title = "" });
        var missingCalendarUpdate = await client.PutAsJsonAsync("/api/events", new
        {
            calendarId = "missing",
            resourceId = "event.ics",
            originalStartsAt = DateTimeOffset.UtcNow,
            title = "Appointment",
            startsAt = DateTimeOffset.UtcNow.AddHours(1),
            endsAt = DateTimeOffset.UtcNow.AddHours(2),
            isAllDay = false
        });
        using var invalidDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/events")
        { Content = JsonContent.Create(new { calendarId = "", resourceId = "" }) };
        var invalidEventDelete = await client.SendAsync(invalidDeleteRequest);
        using var missingDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/events")
        { Content = JsonContent.Create(new { calendarId = "missing", resourceId = "event.ics", originalStartsAt = DateTimeOffset.UtcNow }) };
        var missingCalendarDelete = await client.SendAsync(missingDeleteRequest);
        var shortAddressSearch = await client.GetAsync("/api/locations?query=ab");
        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        var crossSite = await client.SendAsync(crossSiteRequest);

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        homeContent.ShouldContain("Manage iCloud");
        homeContent.ShouldContain("Calendar view");
        homeContent.ShouldContain("data-view=\"month\"");
        homeContent.ShouldContain("app-version");
        homeContent.ShouldContain("header-sync");
        homeContent.ShouldContain("calendar-filter");
        homeContent.ShouldContain("favicon-32.png");
        homeContent.ShouldContain("app.webmanifest");
        homeContent.ShouldNotContain("AT A GLANCE");
        homeContent.ShouldNotContain("id=\"sync-now\"");
        home.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        favicon.StatusCode.ShouldBe(HttpStatusCode.OK);
        favicon.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        manifest.StatusCode.ShouldBe(HttpStatusCode.OK);
        manifestContent.ShouldContain("icon-192.png");
        manifestContent.ShouldContain("icon-512.png");
        accounts.ShouldBeEmpty();
        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sync.Content.ReadAsStringAsync()).ShouldContain("\"succeeded\":true");
        invalidAgenda.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalidEvent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missingCalendarEvent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        invalidEventUpdate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missingCalendarUpdate.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        invalidEventDelete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missingCalendarDelete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        shortAddressSearch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalidAgenda.Headers.CacheControl?.NoStore.ShouldBeTrue();
        crossSite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
