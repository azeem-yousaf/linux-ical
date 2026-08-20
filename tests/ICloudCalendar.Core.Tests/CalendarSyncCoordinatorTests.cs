using ICloudCalendar.Web.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalendarSyncCoordinatorTests
{
    [Fact]
    public async Task SyncAllAsyncIsolatesAccountsPublishesStatusAndRemovesStaleEntries()
    {
        var accounts = Substitute.For<IAccountCatalog>();
        var synchronizer = Substitute.For<IAccountSynchronizer>();
        var timeProvider = Substitute.For<TimeProvider>();
        var logger = Substitute.For<ILogger<CalendarSyncCoordinator>>();
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var work = new CalendarAccount("work-account", "work@icloud.com");
        var home = new CalendarAccount("home-account", "home@icloud.com");
        accounts.GetAccountsAsync(Arg.Any<CancellationToken>()).Returns([work, home]);
        synchronizer.SyncAsync(work.Id, Arg.Any<CancellationToken>())
            .Returns([new CalendarSyncOutcome("work", true)]);
        synchronizer.SyncAsync(home.Id, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CalendarSyncOutcome>>(_ => throw new InvalidOperationException("locked keyring"));
        timeProvider.GetUtcNow().Returns(now);
        var coordinator = new CalendarSyncCoordinator(accounts, synchronizer, timeProvider, logger);

        var result = await coordinator.SyncAllAsync();

        result.ShouldBeFalse();
        var statuses = coordinator.GetAll();
        statuses.Select(item => (item.AccountId, item.AttemptedAt, item.Succeeded)).ShouldBe([
            (home.Id, now, false),
            (work.Id, now, true)
        ]);
        statuses[0].Calendars.ShouldBe([new CalendarSyncOutcome(string.Empty, false, "sync_failed")]);
        statuses[1].Calendars.ShouldBe([new CalendarSyncOutcome("work", true)]);

        accounts.GetAccountsAsync(Arg.Any<CancellationToken>()).Returns([]);
        (await coordinator.SyncAllAsync()).ShouldBeTrue();
        coordinator.GetAll().ShouldBeEmpty();
    }
}
