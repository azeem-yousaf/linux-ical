namespace ICloudCalendar.Core;

public sealed record CalendarChange(string RemoteId, IReadOnlyList<CalendarEvent>? Events)
{
    public bool IsDeletion => Events is null;
}

public sealed record SyncPage(
    IReadOnlyList<CalendarChange> Changes,
    string? NextPageCursor,
    string? NextSyncToken);

public sealed record SyncCheckpoint(string? SyncToken, DateTimeOffset? LastSuccessfulSync);

public sealed record SyncResult(int Upserted, int Deleted, string? SyncToken, DateTimeOffset CompletedAt);

public sealed class SyncTokenRejectedException(string message) : Exception(message);

public interface ICalendarChangeSource
{
    Task<SyncPage> GetChangesAsync(string calendarId, string? syncToken, string? pageCursor, CancellationToken cancellationToken);
}

public interface ICalendarStore
{
    Task<SyncCheckpoint> GetCheckpointAsync(string calendarId, CancellationToken cancellationToken);
    Task ApplyAsync(
        string calendarId,
        IReadOnlyList<CalendarEvent> upserts,
        IReadOnlyList<string> deletions,
        bool replaceExisting,
        string? syncToken,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IAgendaReader
{
    Task<IReadOnlyList<CalendarEvent>> GetAgendaAsync(
        DateTimeOffset startsBefore,
        DateTimeOffset endsAfter,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
