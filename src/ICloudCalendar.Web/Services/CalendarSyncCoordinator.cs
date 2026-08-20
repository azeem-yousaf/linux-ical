using System.Collections.Concurrent;
using ICloudCalendar.Core;

namespace ICloudCalendar.Web.Services;

public interface ICalendarSyncCoordinator : ISyncStatusReader
{
    Task<bool> SyncAllAsync(CancellationToken cancellationToken = default);
}

public sealed class CalendarSyncCoordinator(
    IAccountCatalog accounts,
    IAccountSynchronizer synchronizer,
    TimeProvider timeProvider,
    ILogger<CalendarSyncCoordinator> logger) : ICalendarSyncCoordinator
{
    private static readonly Action<ILogger, string, Exception?> LogAccountSyncFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1002, "AccountSyncFailed"),
        "Calendar synchronization failed for account {AccountId}.");
    private readonly ConcurrentDictionary<string, AccountSyncStatus> _status = new(StringComparer.Ordinal);

    public IReadOnlyList<AccountSyncStatus> GetAll() => _status.Values
        .OrderBy(item => item.AccountId, StringComparer.Ordinal)
        .ToArray();

    public async Task<bool> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var accountList = await accounts.GetAccountsAsync(cancellationToken);
        var activeAccountIds = accountList.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleAccountId in _status.Keys.Where(id => !activeAccountIds.Contains(id)))
        {
            _status.TryRemove(staleAccountId, out _);
        }

        var succeeded = true;
        foreach (var account in accountList)
        {
            IReadOnlyList<CalendarSyncOutcome> outcomes;
            try
            {
                outcomes = await synchronizer.SyncAsync(account.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogAccountSyncFailed(logger, account.Id, exception);
                outcomes = [new CalendarSyncOutcome(string.Empty, false, "sync_failed")];
            }

            var accountSucceeded = outcomes.All(item => item.Succeeded);
            succeeded &= accountSucceeded;
            _status[account.Id] = new AccountSyncStatus(
                account.Id,
                timeProvider.GetUtcNow(),
                accountSucceeded,
                outcomes);
        }

        return succeeded;
    }
}
