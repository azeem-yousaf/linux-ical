namespace ICloudCalendar.Core;

public sealed record CalendarAccount(string Id, string UserName);

public sealed record CalendarSubscription(
    string Id,
    string AccountId,
    string DisplayName,
    Uri RemoteUri,
    string? Color,
    bool IsEnabled = true);

public interface IAccountCatalog
{
    Task SaveAsync(
        CalendarAccount account,
        IReadOnlyList<CalendarSubscription> calendars,
        CancellationToken cancellationToken = default);

    Task<CalendarAccount?> GetAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarSubscription>> GetCalendarsAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarSubscription>> GetAllCalendarsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string accountId, CancellationToken cancellationToken = default);
}

public sealed record CalendarSyncOutcome(string CalendarId, bool Succeeded, string? ErrorCode = null);

public interface IAccountSynchronizer
{
    Task<IReadOnlyList<CalendarSyncOutcome>> SyncAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}

public sealed record NewCalendarEvent(
    string CalendarId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? Location = null,
    string? Description = null);

public interface ICalendarEventWriter
{
    Task CreateAsync(NewCalendarEvent calendarEvent, CancellationToken cancellationToken = default);
}

public interface IProjectionMaintenance
{
    Task<bool> PrepareIfDueAsync(string calendarId, CancellationToken cancellationToken = default);
    Task MarkCompletedAsync(string calendarId, CancellationToken cancellationToken = default);
}
