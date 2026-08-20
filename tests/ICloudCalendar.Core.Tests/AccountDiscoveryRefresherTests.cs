using ICloudCalendar.Infrastructure.CalDav;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AccountDiscoveryRefresherTests
{
    private readonly IAppleCalendarProbe _probe = Substitute.For<IAppleCalendarProbe>();
    private readonly IAccountCatalog _accounts = Substitute.For<IAccountCatalog>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    public AccountDiscoveryRefresherTests() => _timeProvider.GetUtcNow().Returns(_ => _now);

    [Fact]
    public async Task RefreshIfDueAsyncDiscoversOncePerIntervalAndPersistsTheCurrentCalendarSet()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var calendar = new DiscoveredCalendar(
            "work",
            "Work",
            new Uri("https://p01-caldav.icloud.com/calendars/work/"),
            "#00FF00FF",
            "token");
        _probe.DiscoverAsync(account.UserName, "secret", Arg.Any<CancellationToken>()).Returns([calendar]);
        var refresher = Sut();

        await refresher.RefreshIfDueAsync(account, "secret");
        await refresher.RefreshIfDueAsync(account, "secret");

        await _probe.Received(1).DiscoverAsync(account.UserName, "secret", Arg.Any<CancellationToken>());
        await _accounts.Received(1).SaveAsync(
            account,
            Arg.Is<IReadOnlyList<CalendarSubscription>>(items =>
                items.Count == 1 && items[0].Id == calendar.Id && items[0].Color == calendar.Color),
            Arg.Any<CancellationToken>());

        _now = _now.AddHours(7);
        await refresher.RefreshIfDueAsync(account, "secret");
        await _probe.Received(2).DiscoverAsync(account.UserName, "secret", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshIfDueAsyncBacksOffAfterRemoteFailure()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        _probe.DiscoverAsync(account.UserName, "secret", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DiscoveredCalendar>>(_ => throw new HttpRequestException("offline"));
        var refresher = Sut();

        await Should.ThrowAsync<HttpRequestException>(() => refresher.RefreshIfDueAsync(account, "secret"));
        await refresher.RefreshIfDueAsync(account, "secret");

        await _probe.Received(1).DiscoverAsync(account.UserName, "secret", Arg.Any<CancellationToken>());
        await _accounts.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task MarkCurrentPreventsDuplicateDiscoveryImmediatelyAfterOnboarding()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var refresher = Sut();

        refresher.MarkCurrent(account.Id);
        await refresher.RefreshIfDueAsync(account, "secret");

        await _probe.DidNotReceiveWithAnyArgs().DiscoverAsync(default!, default!, default);
    }

    private AccountDiscoveryRefresher Sut() => new(_probe, _accounts, _timeProvider);
}
