using ICloudCalendar.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SqliteCalendarStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"icloud-calendar-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ApplyAndAgendaRoundTripEventsInChronologicalOrder()
    {
        var store = CreateStore();
        var later = Event("later", 14, title: "Lunch", location: "Cafe");
        var earlier = Event("earlier", 9, title: "Stand-up", notes: "Daily notes");
        var completedAt = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        await store.ApplyAsync("work", [later, earlier], [], false, "token-1", completedAt, CancellationToken.None);
        var agenda = await store.GetAgendaAsync(
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));

        agenda.ShouldBe([earlier, later]);
        (await store.GetCheckpointAsync("work", CancellationToken.None))
            .ShouldBe(new SyncCheckpoint("token-1", completedAt));
    }

    [Fact]
    public async Task ApplyReplacesExistingResourceAndDeletesTombstone()
    {
        var store = CreateStore();
        var first = Event("meeting", 9);
        await store.ApplyAsync("work", [first, Event("obsolete", 12)], [], false, "token-1", first.StartsAt, CancellationToken.None);
        var updated = first with { ETag = "etag-2", Title = "Updated meeting", StartsAt = first.StartsAt.AddHours(1), EndsAt = first.EndsAt.AddHours(1) };

        await store.ApplyAsync("work", [updated], ["obsolete"], false, "token-2", updated.StartsAt, CancellationToken.None);
        var agenda = await store.GetAgendaAsync(updated.StartsAt.AddDays(1), updated.StartsAt.AddDays(-1));

        agenda.ShouldBe([updated]);
        (await store.GetCheckpointAsync("work", CancellationToken.None)).SyncToken.ShouldBe("token-2");
    }

    [Fact]
    public async Task ApplyRollsBackEventsAndCheckpointWhenAnyEventIsInvalid()
    {
        var store = CreateStore();
        var original = Event("meeting", 9);
        await store.ApplyAsync("work", [original], [], false, "token-1", original.StartsAt, CancellationToken.None);
        var replacement = original with { Title = "Should roll back", ETag = "etag-2" };
        var invalid = Event("invalid", 11) with { EndsAt = Event("invalid", 11).StartsAt };

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.ApplyAsync(
            "work", [replacement, invalid], [], true, "token-2", replacement.StartsAt, CancellationToken.None));

        var agenda = await store.GetAgendaAsync(original.StartsAt.AddDays(1), original.StartsAt.AddDays(-1));
        agenda.ShouldBe([original]);
        (await store.GetCheckpointAsync("work", CancellationToken.None)).SyncToken.ShouldBe("token-1");
    }

    [Fact]
    public async Task AgendaIncludesEventsOverlappingRangeBoundaries()
    {
        var store = CreateStore();
        var overnight = Event("overnight", 23) with
        {
            EndsAt = new DateTimeOffset(2026, 8, 21, 2, 0, 0, TimeSpan.Zero)
        };
        await store.ApplyAsync("work", [overnight], [], false, "token-1", overnight.StartsAt, CancellationToken.None);

        var agenda = await store.GetAgendaAsync(
            new DateTimeOffset(2026, 8, 21, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

        agenda.ShouldBe([overnight]);
    }

    [Fact]
    public async Task ApplyReplacesAndDeletesAllOccurrencesForRecurringResource()
    {
        var store = CreateStore();
        var first = Event("series::1", 9) with { SourceRemoteId = "series.ics" };
        var second = Event("series::2", 10) with { SourceRemoteId = "series.ics" };
        await store.ApplyAsync("work", [first, second], [], false, "token-1", first.StartsAt, CancellationToken.None);
        var replacement = first with { Title = "Updated series", ETag = "etag-2" };

        await store.ApplyAsync("work", [replacement], [], false, "token-2", replacement.StartsAt, CancellationToken.None);
        var afterReplacement = await store.GetAgendaAsync(first.StartsAt.AddDays(1), first.StartsAt.AddDays(-1));
        afterReplacement.ShouldBe([replacement]);

        await store.ApplyAsync("work", [], ["series.ics"], false, "token-3", replacement.StartsAt, CancellationToken.None);
        (await store.GetAgendaAsync(first.StartsAt.AddDays(1), first.StartsAt.AddDays(-1))).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyReplacingRecurringResourceWithSingleEventRemovesOldOccurrences()
    {
        var store = CreateStore();
        var first = Event("series::1", 9) with { SourceRemoteId = "series.ics" };
        var second = Event("series::2", 10) with { SourceRemoteId = "series.ics" };
        await store.ApplyAsync("work", [first, second], [], false, "token-1", first.StartsAt, CancellationToken.None);
        var replacement = Event("series.ics", 11) with { Title = "Now a single event", ETag = "etag-2" };

        await store.ApplyAsync("work", [replacement], [], false, "token-2", replacement.StartsAt, CancellationToken.None);

        var agenda = await store.GetAgendaAsync(replacement.StartsAt.AddDays(1), first.StartsAt.AddDays(-1));
        agenda.ShouldBe([replacement]);
    }

    [Fact]
    public async Task FullApplyAtomicallyRemovesStaleEventsOnlyFromTargetCalendar()
    {
        var store = CreateStore();
        var stale = Event("stale", 9);
        var otherCalendar = Event("personal", 11) with { CalendarId = "personal" };
        await store.ApplyAsync("work", [stale], [], false, "old-work-token", stale.StartsAt, CancellationToken.None);
        await store.ApplyAsync("personal", [otherCalendar], [], false, "personal-token", stale.StartsAt, CancellationToken.None);
        var current = Event("current", 10);

        await store.ApplyAsync("work", [current], [], true, "new-work-token", current.StartsAt, CancellationToken.None);

        var agenda = await store.GetAgendaAsync(current.StartsAt.AddDays(1), current.StartsAt.AddDays(-1));
        agenda.ShouldBe([current, otherCalendar]);
    }

    [Fact]
    public async Task AgendaRangeQueryUsesDedicatedIndex()
    {
        var store = CreateStore();
        await store.GetCheckpointAsync("work", CancellationToken.None);
        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT calendar_id, remote_id
            FROM calendar_events
            WHERE starts_at_ms < $range_end AND ends_at_ms > $range_start
            ORDER BY starts_at_ms, ends_at_ms
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$range_end", long.MaxValue);
        command.Parameters.AddWithValue("$range_start", 0L);
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        details.ShouldContain(item => item.Contains("ix_calendar_events_agenda", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SqliteCalendarStore CreateStore()
    {
        var connections = new SqliteConnectionFactory($"Data Source={_databasePath};Pooling=False");
        return new SqliteCalendarStore(connections, new SqliteDatabaseInitializer(connections));
    }

    private static CalendarEvent Event(
        string id,
        int hour,
        string title = "Meeting",
        string? location = null,
        string? notes = null)
    {
        var startsAt = new DateTimeOffset(2026, 8, 20, hour, 0, 0, TimeSpan.Zero);
        return new CalendarEvent(
            "work",
            id,
            "etag-1",
            title,
            startsAt,
            startsAt.AddHours(1),
            Location: location,
            Notes: notes);
    }
}
