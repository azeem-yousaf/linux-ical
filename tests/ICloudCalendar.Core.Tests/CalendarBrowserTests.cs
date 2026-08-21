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
    public async Task UpdateBannerOffersReleaseDetailsAndInstallsInPlace()
    {
        var page = await _browser!.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1000, Height = 760 }
        });
        await MockLocalApiAsync(page, offerUpdate: true);
        await page.GotoAsync(_applicationUri!.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.Locator("#update-banner")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "View release" })).ToHaveAttributeAsync("href", "https://github.com/azeem-yousaf/linux-ical/releases/tag/v1.2.0");
        await page.GetByRole(AriaRole.Button, new() { Name = "Update now" }).ClickAsync();
        await Assertions.Expect(page.Locator("#update-copy")).ToContainTextAsync("Installing now");
        await Assertions.Expect(page.Locator("#app-version")).ToHaveTextAsync("v1.2.0", new() { Timeout = 5000 });
        await Assertions.Expect(page.Locator("#update-banner")).ToBeHiddenAsync();
    }

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
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Week" })).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit Design review" })).ToBeVisibleAsync();
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/linux-icloud-calendar-week.png", FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Day", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Design review" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Studio · 1h")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Work", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#event-count")).ToHaveTextAsync("1 event");
        await Assertions.Expect(page.Locator("#connect-button")).ToContainTextAsync("1 calendar connected");

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Design review" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Update event" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("input[name=title]")).ToHaveValueAsync("Design review");
        await Assertions.Expect(page.Locator("select[name=calendarId]")).ToBeDisabledAsync();
        await page.Locator("#event-dialog").ScreenshotAsync(new LocatorScreenshotOptions { Path = "/tmp/linux-icloud-event-update-desktop.png" });
        await page.Locator("input[name=title]").FillAsync("Updated design review");
        var updateRequestTask = page.WaitForRequestAsync(request => request.Method == "PUT" && request.Url.EndsWith("/api/events", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync();
        var updateRequest = await updateRequestTask;
        var updateBody = updateRequest.PostData ?? string.Empty;
        updateBody.ShouldContain("\"resourceId\":\"design-review.ics\"");
        updateBody.ShouldContain("\"originalStartsAt\"");
        updateBody.ShouldContain("\"title\":\"Updated design review\"");
        await Assertions.Expect(page.Locator("#event-form .form-status")).ToHaveTextAsync("Updated in iCloud.");
        await Assertions.Expect(page.Locator("#event-dialog")).ToBeHiddenAsync(new() { Timeout = 2000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Design review" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Delete event" })).ToBeVisibleAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var deleteRequestTask = page.WaitForRequestAsync(request => request.Method == "DELETE" && request.Url.EndsWith("/api/events", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete event" }).ClickAsync();
        var deleteRequest = await deleteRequestTask;
        var deleteBody = deleteRequest.PostData ?? string.Empty;
        deleteBody.ShouldContain("\"resourceId\":\"design-review.ics\"");
        deleteBody.ShouldContain("\"originalStartsAt\"");
        await Assertions.Expect(page.Locator("#event-form .form-status")).ToHaveTextAsync("Deleted from iCloud.");
        await Assertions.Expect(page.Locator("#event-dialog")).ToBeHiddenAsync(new() { Timeout = 2000 });

        await page.Locator("#create-event").ClickAsync();
        var eventDialog = page.Locator("#event-dialog");
        await Assertions.Expect(eventDialog).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#event-delete")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add to calendar" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("input[name=startsAt]")).Not.ToHaveValueAsync(string.Empty);
        await Assertions.Expect(page.Locator("input[name=endsAt]")).Not.ToHaveValueAsync(string.Empty);
        var dialogBounds = await eventDialog.BoundingBoxAsync();
        dialogBounds.ShouldNotBeNull();
        dialogBounds.Width.ShouldBeGreaterThan(620);
        dialogBounds.Height.ShouldBeLessThan(900);
        await eventDialog.ScreenshotAsync(new LocatorScreenshotOptions { Path = "/tmp/linux-icloud-event-editor-desktop.png" });

        await page.Locator("input[name=location]").FillAsync("89 Bath Road");
        await Assertions.Expect(page.Locator("#address-suggestions [role=option]")).ToContainTextAsync("Tesco Express");
        await eventDialog.ScreenshotAsync(new LocatorScreenshotOptions { Path = "/tmp/linux-icloud-event-editor-address.png" });
        await page.Locator("input[name=location]").PressAsync("ArrowDown");
        await page.Locator("input[name=location]").PressAsync("Enter");
        await Assertions.Expect(page.Locator("input[name=location]")).ToHaveValueAsync("Tesco Express, 89 Bath Road, Bristol");
        await page.Locator("input[name=title]").FillAsync("All-day planning");
        await page.Locator(".all-day-toggle").ClickAsync();
        await Assertions.Expect(page.Locator("#event-end-field")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("#start-label")).ToHaveTextAsync("Date");
        await Assertions.Expect(page.Locator("input[name=endsAt]")).ToBeDisabledAsync();
        var createRequestTask = page.WaitForRequestAsync(request => request.Method == "POST" && request.Url.EndsWith("/api/events", StringComparison.Ordinal));
        await page.Locator("#event-submit").ClickAsync();
        var createRequest = await createRequestTask;
        var createBody = createRequest.PostData ?? string.Empty;
        createBody.ShouldContain("\"title\":\"All-day planning\"");
        createBody.ShouldContain("\"isAllDay\":true");
        await Assertions.Expect(page.Locator("#event-form .form-status")).ToHaveTextAsync("Added to iCloud.");
        await Assertions.Expect(eventDialog).ToBeHiddenAsync();

        await page.Locator("#connect-button").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Manage iCloud" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Change password" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Change credentials" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("input[name=userName]")).ToHaveValueAsync("person@example.com");
        await Assertions.Expect(page.Locator("input[name=appSpecificPassword]")).ToHaveValueAsync(string.Empty);

        await page.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Week", Exact = true }).ClickAsync();
        var thisWeekTitle = await page.Locator("#agenda-title").TextContentAsync();
        await page.Locator("#next-period").ClickAsync();
        await Assertions.Expect(page.Locator("#agenda-title")).Not.ToHaveTextAsync(thisWeekTitle!);
        await page.Locator("#today-button").ClickAsync();
        await Assertions.Expect(page.Locator("#agenda-title")).ToHaveTextAsync(thisWeekTitle!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Month", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".month-cells .month-day")).ToHaveCountAsync(42);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/linux-icloud-calendar-month.png", FullPage = true });
        await page.Locator(".month-day.is-today .month-date").ClickAsync();
        await Assertions.Expect(page.Locator("#agenda-title")).ToHaveTextAsync("Today");
        await page.GetByRole(AriaRole.Button, new() { Name = "Week", Exact = true }).ClickAsync();

        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Week" })).ToHaveAttributeAsync("aria-pressed", "true");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/linux-icloud-calendar-week-mobile.png", FullPage = true });
        var greetingBounds = await page.Locator("#greeting").BoundingBoxAsync();
        var connectBounds = await page.Locator("#connect-button").BoundingBoxAsync();
        greetingBounds.ShouldNotBeNull();
        connectBounds.ShouldNotBeNull();
        Math.Abs(
            greetingBounds.Y + greetingBounds.Height / 2
            - (connectBounds.Y + connectBounds.Height / 2)).ShouldBeLessThan(8);
        connectBounds.Width.ShouldBeGreaterThan(60);
        var hasPageOverflow = await page.EvaluateAsync<bool>(
            "document.documentElement.scrollWidth > document.documentElement.clientWidth");
        hasPageOverflow.ShouldBeFalse();

        await page.Locator("#create-event").ClickAsync();
        await Assertions.Expect(page.Locator("#event-dialog")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#event-form .form-status")).ToBeEmptyAsync();
        await page.Locator("#event-dialog").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = "/tmp/linux-icloud-event-editor-mobile.png"
        });
        var mobileDialogBounds = await page.Locator("#event-dialog").BoundingBoxAsync();
        mobileDialogBounds.ShouldNotBeNull();
        mobileDialogBounds.Width.ShouldBeLessThanOrEqualTo(390);
        mobileDialogBounds.Height.ShouldBeLessThanOrEqualTo(828);
        await Assertions.Expect(page.Locator("#event-form button[type=submit]")).ToBeVisibleAsync();
        await page.Locator("input[name=location]").FillAsync("89 Bath Road");
        await Assertions.Expect(page.Locator("#address-suggestions [role=option]")).ToContainTextAsync("Tesco Express");
        await page.Locator("#event-dialog").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = "/tmp/linux-icloud-event-editor-address-mobile.png"
        });

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

    private static async Task MockLocalApiAsync(IPage page, bool offerUpdate = false)
    {
        var updateStarted = false;
        await page.RouteAsync("**/api/update/install", route =>
        {
            updateStarted = true;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 202,
                ContentType = "application/json",
                Body = "{\"updating\":true,\"latestVersion\":\"1.2.0\"}"
            });
        });
        await page.RouteAsync("**/api/update", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = offerUpdate && !updateStarted
                ? "{\"currentVersion\":\"1.1.0\",\"latestVersion\":\"1.2.0\",\"updateAvailable\":true,\"releaseUrl\":\"https://github.com/azeem-yousaf/linux-ical/releases/tag/v1.2.0\"}"
                : "{\"currentVersion\":\"1.2.0\",\"latestVersion\":\"1.2.0\",\"updateAvailable\":false,\"releaseUrl\":\"https://github.com/azeem-yousaf/linux-ical/releases/tag/v1.2.0\"}"
        }));
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
        await page.RouteAsync("**/api/locations?*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = "[{\"label\":\"Tesco Express, 89 Bath Road, Bristol\",\"primary\":\"Tesco Express\",\"secondary\":\"89 Bath Road, Bristol\"}]"
        }));
        await page.RouteAsync("**/api/events", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = "{\"updated\":true}"
        }));
        await page.RouteAsync("**/api/widget/agenda?*", async route =>
        {
            var startsAt = DateTimeOffset.Now.Date.AddHours(10);
            var body = $$"""
                {"events":[{"id":"event-1","resourceId":"design-review.ics","originalStartsAt":"{{startsAt:O}}","calendarId":"work","calendarName":"Work","color":"#79e6c4","title":"Design review","startsAt":"{{startsAt:O}}","endsAt":"{{startsAt.AddHours(1):O}}","isAllDay":false,"location":"Studio","description":"Review the new calendar"}]}
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
