using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalendarBrowserTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"icloud-calendar-browser-{Guid.NewGuid():N}.db");
    private readonly List<string> _browserErrors = [];
    private Process? _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Uri? _applicationUri;

    [Fact]
    public async Task CalendarUiRendersAgendaAndProtectsCredentials()
    {
        var page = await _browser!.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        page.Console += (_, message) =>
        {
            if (StringComparer.Ordinal.Equals(message.Type, "error"))
            {
                _browserErrors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => _browserErrors.Add(error);
        await MockLocalApiAsync(page);

        var response = await page.GotoAsync(
            _applicationUri!.AbsoluteUri,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        response.ShouldNotBeNull();
        response.Status.ShouldBe((int)HttpStatusCode.OK);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Design review" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Studio · 1h")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Work", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#event-count")).ToHaveTextAsync("1 event");
        await Assertions.Expect(page.Locator("#connect-button")).ToContainTextAsync("1 calendar connected");

        await page.Locator("#connect-button").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Manage iCloud" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Change password" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Change credentials" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("input[name=userName]")).ToHaveValueAsync("person@example.com");
        await Assertions.Expect(page.Locator("input[name=appSpecificPassword]")).ToHaveValueAsync(string.Empty);

        await page.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await page.Locator("#next-week").ClickAsync();
        await Assertions.Expect(page.Locator("#agenda-title")).Not.ToHaveTextAsync("Today");
        await page.Locator("#today-button").ClickAsync();
        await Assertions.Expect(page.Locator("#agenda-title")).ToHaveTextAsync("Today");

        _browserErrors.ShouldBeEmpty();
    }

    public async Task InitializeAsync()
    {
        var externalUri = Environment.GetEnvironmentVariable("CALENDAR_E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(externalUri))
        {
            var port = ReserveLoopbackPort();
            _applicationUri = new Uri($"http://127.0.0.1:{port}/");
            _server = StartApplication(port);
        }
        else
        {
            _applicationUri = new Uri(externalUri, UriKind.Absolute);
            if (!_applicationUri.IsLoopback)
            {
                throw new InvalidOperationException("Browser tests may target only a loopback application URL.");
            }
        }

        await WaitUntilHealthyAsync(_applicationUri);

        _playwright = await Playwright.CreateAsync();
        var localChromium = File.Exists("/usr/bin/chromium") ? "/usr/bin/chromium" : null;
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = localChromium,
            Args = ["--no-sandbox"]
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
            await _server.WaitForExitAsync();
        }

        _server?.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private static async Task MockLocalApiAsync(IPage page)
    {
        await page.RouteAsync("**/api/accounts", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """
                [{"id":"account-1","userName":"person@example.com","calendars":[{"id":"work","displayName":"Work","color":"#79e6c4","isEnabled":true}]}]
                """
        }));
        await page.RouteAsync("**/api/sync/status", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = "[]"
        }));
        await page.RouteAsync("**/api/widget/agenda?*", async route =>
        {
            var startsAt = DateTimeOffset.Now.Date.AddHours(10);
            var body = $$"""
                {"events":[{"id":"event-1","calendarId":"work","calendarName":"Work","color":"#79e6c4","title":"Design review","startsAt":"{{startsAt:O}}","endsAt":"{{startsAt.AddHours(1):O}}","isAllDay":false,"location":"Studio"}]}
                """;
            await route.FulfillAsync(new RouteFulfillOptions { ContentType = "application/json", Body = body });
        });
    }

    private Process StartApplication(int port)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(dotnetHost, $"\"{typeof(Program).Assembly.Location}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["Calendar__DatabasePath"] = _databasePath;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the calendar web application.");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilHealthyAsync(Uri applicationUri)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var healthUri = new Uri(applicationUri, "health");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(healthUri);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The calendar application did not become healthy for browser testing.");
    }
}
