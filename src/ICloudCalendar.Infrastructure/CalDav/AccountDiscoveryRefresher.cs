using System.Collections.Concurrent;
using ICloudCalendar.Core;

namespace ICloudCalendar.Infrastructure.CalDav;

public interface IAccountDiscoveryRefresher
{
    void MarkCurrent(string accountId);
    Task RefreshIfDueAsync(
        CalendarAccount account,
        string appSpecificPassword,
        CancellationToken cancellationToken = default);
}

public sealed class AccountDiscoveryRefresher(
    IAppleCalendarProbe probe,
    IAccountCatalog accounts,
    TimeProvider timeProvider) : IAccountDiscoveryRefresher
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextRefresh = new(StringComparer.Ordinal);

    public void MarkCurrent(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _nextRefresh[accountId] = timeProvider.GetUtcNow() + RefreshInterval;
    }

    public async Task RefreshIfDueAsync(
        CalendarAccount account,
        string appSpecificPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSpecificPassword);
        var now = timeProvider.GetUtcNow();
        if (_nextRefresh.TryGetValue(account.Id, out var nextRefresh) && nextRefresh > now)
        {
            return;
        }

        try
        {
            var calendars = await probe.DiscoverAsync(
                account.UserName,
                appSpecificPassword,
                cancellationToken);
            await accounts.SaveAsync(
                account,
                calendars.Select(item => new CalendarSubscription(
                    item.Id,
                    account.Id,
                    item.DisplayName,
                    item.Uri,
                    item.Color)).ToArray(),
                cancellationToken);
            _nextRefresh[account.Id] = timeProvider.GetUtcNow() + RefreshInterval;
        }
        catch
        {
            _nextRefresh[account.Id] = timeProvider.GetUtcNow() + FailureRetryInterval;
            throw;
        }
    }
}
