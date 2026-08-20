using ICloudCalendar.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SqliteAccountCatalogTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"icloud-accounts-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveAsyncRoundTripsAccountAndReplacesDiscoveredCalendars()
    {
        var catalog = CreateCatalog();
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", account.Id, "Work");
        var personal = Calendar("personal", account.Id, "Personal");
        await catalog.SaveAsync(account, [work, personal]);

        await catalog.SaveAsync(account, [personal with { DisplayName = "Home", Color = "#FF00FF" }]);

        (await catalog.GetAccountAsync(account.Id)).ShouldBe(account);
        (await catalog.GetAccountsAsync()).ShouldBe([account]);
        (await catalog.GetCalendarsAsync(account.Id)).ShouldBe([
            personal with { DisplayName = "Home", Color = "#FF00FF" }
        ]);
        (await catalog.GetAllCalendarsAsync()).ShouldBe([
            personal with { DisplayName = "Home", Color = "#FF00FF" }
        ]);
    }

    [Fact]
    public async Task SaveAsyncRejectsCalendarOwnedByDifferentAccountWithoutWritingAccount()
    {
        var catalog = CreateCatalog();
        var account = new CalendarAccount("account-1", "person@icloud.com");

        await Should.ThrowAsync<ArgumentException>(() => catalog.SaveAsync(
            account,
            [Calendar("work", "another-account", "Work")]));

        (await catalog.GetAccountAsync(account.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsyncRemovesCachedStateForCalendarsNoLongerDiscovered()
    {
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        var database = new SqliteDatabaseInitializer(connections);
        var catalog = new SqliteAccountCatalog(connections, database);
        var store = new SqliteCalendarStore(connections, database);
        var account = new CalendarAccount("account-1", "person@icloud.com");
        await catalog.SaveAsync(account, [Calendar("work", account.Id, "Work"), Calendar("home", account.Id, "Home")]);
        var calendarEvent = new CalendarEvent(
            "work", "event-1", "etag-1", "Removed calendar event",
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        await store.ApplyAsync("work", [calendarEvent], [], false, "token-1", calendarEvent.StartsAt, CancellationToken.None);

        await catalog.SaveAsync(account, [Calendar("home", account.Id, "Home")]);

        (await store.GetAgendaAsync(calendarEvent.EndsAt.AddDays(1), calendarEvent.StartsAt.AddDays(-1))).ShouldBeEmpty();
        (await store.GetCheckpointAsync("work", CancellationToken.None)).ShouldBe(new SyncCheckpoint(null, null));
    }

    [Fact]
    public async Task DeleteAsyncRemovesAccountCalendarsEventsAndSyncState()
    {
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        var database = new SqliteDatabaseInitializer(connections);
        var catalog = new SqliteAccountCatalog(connections, database);
        var store = new SqliteCalendarStore(connections, database);
        var account = new CalendarAccount("account-1", "person@icloud.com");
        await catalog.SaveAsync(account, [Calendar("work", account.Id, "Work")]);
        var calendarEvent = new CalendarEvent(
            "work", "event-1", "etag-1", "Private meeting",
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        await store.ApplyAsync("work", [calendarEvent], [], false, "token-1", calendarEvent.StartsAt, CancellationToken.None);

        await catalog.DeleteAsync(account.Id);

        (await catalog.GetAccountAsync(account.Id)).ShouldBeNull();
        (await catalog.GetCalendarsAsync(account.Id)).ShouldBeEmpty();
        (await store.GetAgendaAsync(calendarEvent.EndsAt.AddDays(1), calendarEvent.StartsAt.AddDays(-1))).ShouldBeEmpty();
        (await store.GetCheckpointAsync("work", CancellationToken.None)).ShouldBe(new SyncCheckpoint(null, null));
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SqliteAccountCatalog CreateCatalog()
    {
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        return new SqliteAccountCatalog(connections, new SqliteDatabaseInitializer(connections));
    }

    private static CalendarSubscription Calendar(string id, string accountId, string name) => new(
        id,
        accountId,
        name,
        new Uri($"https://p01-caldav.icloud.com/calendars/{id}/"),
        null);
}
