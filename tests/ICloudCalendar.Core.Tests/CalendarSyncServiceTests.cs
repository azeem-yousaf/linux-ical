using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class CalendarSyncServiceTests
{
    private readonly ICalendarChangeSource _source = Substitute.For<ICalendarChangeSource>();
    private readonly ICalendarStore _store = Substitute.For<ICalendarStore>();
    private readonly IClock _clock = Substitute.For<IClock>();

    [Fact]
    public async Task SyncAsync_AppliesAllPagesAtomicallyAndAdvancesToken()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var standup = Event("standup", "etag-2");
        _clock.UtcNow.Returns(now);
        _store.GetCheckpointAsync("work", Arg.Any<CancellationToken>())
            .Returns(new SyncCheckpoint("token-1", now.AddMinutes(-1)));
        _source.GetChangesAsync("work", "token-1", null, Arg.Any<CancellationToken>())
            .Returns(new SyncPage([new CalendarChange("old", null)], "page-2", null));
        _source.GetChangesAsync("work", "token-1", "page-2", Arg.Any<CancellationToken>())
            .Returns(new SyncPage([new CalendarChange("standup", [standup])], null, "token-2"));

        var result = await Sut().SyncAsync("work");

        result.ShouldBe(new SyncResult(1, 1, "token-2", now));
        await _store.Received(1).ApplyAsync(
            "work",
            Arg.Is<IReadOnlyList<CalendarEvent>>(events => events.SequenceEqual(new[] { standup })),
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "old" })),
            false,
            "token-2",
            now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_WhenSameEventChangesTwice_AppliesOnlyNewestState()
    {
        var original = Event("standup", "etag-1");
        var updated = original with { ETag = "etag-2", Title = "Daily sync" };
        _store.GetCheckpointAsync("work", Arg.Any<CancellationToken>()).Returns(new SyncCheckpoint(null, null));
        _source.GetChangesAsync("work", null, null, Arg.Any<CancellationToken>())
            .Returns(new SyncPage(
                [new CalendarChange("standup", [original]), new CalendarChange("standup", [updated])],
                null,
                "token-1"));

        await Sut().SyncAsync("work");

        await _store.Received().ApplyAsync(
            "work",
            Arg.Is<IReadOnlyList<CalendarEvent>>(events => events.Count == 1 && events[0] == updated),
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 0),
            true,
            "token-1",
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_WhenSourceFails_DoesNotCommitPartialPage()
    {
        _store.GetCheckpointAsync("work", Arg.Any<CancellationToken>()).Returns(new SyncCheckpoint("token-1", null));
        _source.GetChangesAsync("work", "token-1", null, Arg.Any<CancellationToken>())
            .Returns(new SyncPage([new CalendarChange("standup", [Event("standup", "etag-1")])], "next", null));
        _source.GetChangesAsync("work", "token-1", "next", Arg.Any<CancellationToken>())
            .Returns<Task<SyncPage>>(_ => throw new HttpRequestException("CalDAV unavailable"));

        await Should.ThrowAsync<HttpRequestException>(() => Sut().SyncAsync("work"));

        await _store.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task SyncAsync_WhenChangedSeriesProjectsNoOccurrences_RemovesPreviousProjection()
    {
        var now = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);
        _store.GetCheckpointAsync("family", Arg.Any<CancellationToken>())
            .Returns(new SyncCheckpoint("token-1", now.AddMinutes(-1)));
        _source.GetChangesAsync("family", "token-1", null, Arg.Any<CancellationToken>())
            .Returns(new SyncPage([new CalendarChange("old-series.ics", [])], null, "token-2"));

        var result = await Sut().SyncAsync("family");

        result.ShouldBe(new SyncResult(0, 1, "token-2", now));
        await _store.Received(1).ApplyAsync(
            "family",
            Arg.Is<IReadOnlyList<CalendarEvent>>(events => events.Count == 0),
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "old-series.ics" })),
            false,
            "token-2",
            now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_WhenIncrementalTokenExpires_RetriesAndAtomicallyReplacesFromFullSnapshot()
    {
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var current = Event("current", "etag-2");
        _clock.UtcNow.Returns(now);
        _store.GetCheckpointAsync("work", Arg.Any<CancellationToken>())
            .Returns(new SyncCheckpoint("expired-token", now.AddMinutes(-1)));
        _source.GetChangesAsync("work", "expired-token", null, Arg.Any<CancellationToken>())
            .Returns<Task<SyncPage>>(_ => throw new SyncTokenRejectedException("expired"));
        _source.GetChangesAsync("work", null, null, Arg.Any<CancellationToken>())
            .Returns(new SyncPage([new CalendarChange("current", [current])], null, "fresh-token"));

        var result = await Sut().SyncAsync("work");

        result.ShouldBe(new SyncResult(1, 0, "fresh-token", now));
        await _store.Received(1).ApplyAsync(
            "work",
            Arg.Is<IReadOnlyList<CalendarEvent>>(events => events.SequenceEqual(new[] { current })),
            Arg.Is<IReadOnlyList<string>>(deletions => deletions.Count == 0),
            true,
            "fresh-token",
            now,
            Arg.Any<CancellationToken>());
    }

    private CalendarSyncService Sut() => new(_source, _store, _clock);

    private static CalendarEvent Event(string id, string etag) => new(
        "work",
        id,
        etag,
        "Stand-up",
        new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 20, 9, 15, 0, TimeSpan.Zero));
}
