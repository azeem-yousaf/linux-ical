using ICloudCalendar.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SqliteProjectionMaintenanceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"icloud-maintenance-{Guid.NewGuid():N}.db");
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task FirstPreparationClearsCheckpointAndRemainsDueUntilCompleted()
    {
        var (maintenance, store) = CreateServices();
        var calendarEvent = Event();
        await store.ApplyAsync("work", [calendarEvent], [], false, "token-1", _time.GetUtcNow(), CancellationToken.None);

        (await maintenance.PrepareIfDueAsync("work")).ShouldBeTrue();
        (await store.GetCheckpointAsync("work", CancellationToken.None)).SyncToken.ShouldBeNull();
        (await maintenance.PrepareIfDueAsync("work")).ShouldBeTrue();
    }

    [Fact]
    public async Task CompletedRebuildSurvivesRestartAndBecomesDueAfterThirtyDays()
    {
        var (maintenance, store) = CreateServices();
        await maintenance.MarkCompletedAsync("work");
        await store.ApplyAsync("work", [Event()], [], false, "token-current", _time.GetUtcNow(), CancellationToken.None);

        var (afterRestart, _) = CreateServices();
        (await afterRestart.PrepareIfDueAsync("work")).ShouldBeFalse();

        _time.Advance(TimeSpan.FromDays(31));
        (await afterRestart.PrepareIfDueAsync("work")).ShouldBeTrue();
        (await store.GetCheckpointAsync("work", CancellationToken.None)).SyncToken.ShouldBeNull();
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private (SqliteProjectionMaintenance Maintenance, SqliteCalendarStore Store) CreateServices()
    {
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        var database = new SqliteDatabaseInitializer(connections);
        return (new SqliteProjectionMaintenance(connections, database, _time), new SqliteCalendarStore(connections, database));
    }

    private static CalendarEvent Event() => new(
        "work", "event-1", "etag-1", "Meeting",
        new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
