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
        var accounts = await client.GetFromJsonAsync<IReadOnlyList<object>>("/api/accounts");
        var sync = await client.PostAsync("/api/sync", null);
        var invalidAgenda = await client.GetAsync("/api/widget/agenda?limit=0");
        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        var crossSite = await client.SendAsync(crossSiteRequest);

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        homeContent.ShouldContain("Manage iCloud");
        home.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        accounts.ShouldBeEmpty();
        sync.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sync.Content.ReadAsStringAsync()).ShouldContain("\"succeeded\":true");
        invalidAgenda.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
