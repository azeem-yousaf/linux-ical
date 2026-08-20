using System.Collections.Concurrent;
using System.Net;
using ICloudCalendar.Core;
using ICloudCalendar.Infrastructure.Security;

namespace ICloudCalendar.Infrastructure.CalDav;

public sealed class TimeProviderClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}

public sealed class FixedCalendarEndpointResolver(Uri endpoint) : ICalendarEndpointResolver
{
    public Uri Resolve(string calendarId) => endpoint;
}

public interface IRemoteCalendarSyncSession : IDisposable
{
    Task SyncAsync(CalendarSubscription calendar, CancellationToken cancellationToken);
}

public interface IRemoteCalendarSyncSessionFactory
{
    IRemoteCalendarSyncSession Create(CalendarAccount account, string password);
}

public sealed class HttpRemoteCalendarSyncSessionFactory(
    ICalendarStore store,
    ICalendarPayloadParser payloadParser,
    IClock clock) : IRemoteCalendarSyncSessionFactory
{
    public IRemoteCalendarSyncSession Create(CalendarAccount account, string password) =>
        new HttpRemoteCalendarSyncSession(account, password, store, payloadParser, clock);

    private sealed class HttpRemoteCalendarSyncSession : IRemoteCalendarSyncSession
    {
        private readonly HttpMessageHandler _handler;
        private readonly HttpClient _client;
        private readonly ICalendarStore _store;
        private readonly ICalendarPayloadParser _payloadParser;
        private readonly IClock _clock;

        public HttpRemoteCalendarSyncSession(
            CalendarAccount account,
            string password,
            ICalendarStore store,
            ICalendarPayloadParser payloadParser,
            IClock clock)
        {
            _store = store;
            _payloadParser = payloadParser;
            _clock = clock;
            _handler = new ICloudSafeRedirectHandler(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                },
                AppleBasicAuthentication.Create(account.UserName, password));
            _client = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(45) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxICloudCalendar/0.1");
        }

        public Task SyncAsync(CalendarSubscription calendar, CancellationToken cancellationToken)
        {
            var source = new CalDavCalendarChangeSource(
                new HttpCalDavTransport(_client),
                new FixedCalendarEndpointResolver(calendar.RemoteUri),
                _payloadParser);
            return new CalendarSyncService(source, _store, _clock).SyncAsync(calendar.Id, cancellationToken);
        }

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }

    }
}

public sealed class AppleAccountSynchronizer(
    IAccountCatalog accounts,
    ICredentialVault credentials,
    IRemoteCalendarSyncSessionFactory sessions,
    IProjectionMaintenance projectionMaintenance,
    IAccountDiscoveryRefresher discoveryRefresher) : IAccountSynchronizer
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<CalendarSyncOutcome>> SyncAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var accountLock = _accountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            return await SyncCoreAsync(accountId, cancellationToken);
        }
        finally
        {
            accountLock.Release();
        }
    }

    private async Task<IReadOnlyList<CalendarSyncOutcome>> SyncCoreAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAccountAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("The calendar account does not exist.");
        var password = await credentials.RetrieveAsync(accountId, cancellationToken);
        if (string.IsNullOrEmpty(password))
        {
            return [new CalendarSyncOutcome(string.Empty, false, "credential_missing")];
        }

        try
        {
            await discoveryRefresher.RefreshIfDueAsync(account, password, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or FormatException)
        {
            // Continue syncing already discovered calendars. Discovery has its own
            // backoff and will retry without degrading the fast event-sync path.
        }

        var calendars = (await accounts.GetCalendarsAsync(accountId, cancellationToken))
            .Where(item => item.IsEnabled)
            .ToArray();
        if (calendars.Length == 0)
        {
            return [];
        }

        using var session = sessions.Create(account, password);
        var outcomes = new ConcurrentBag<CalendarSyncOutcome>();

        await Parallel.ForEachAsync(
            calendars,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (calendar, token) =>
            {
                try
                {
                    var rebuilding = await projectionMaintenance.PrepareIfDueAsync(calendar.Id, token);
                    await session.SyncAsync(calendar, token);
                    if (rebuilding)
                    {
                        await projectionMaintenance.MarkCompletedAsync(calendar.Id, token);
                    }
                    outcomes.Add(new CalendarSyncOutcome(calendar.Id, true));
                }
                catch (HttpRequestException exception) when (
                    exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    outcomes.Add(new CalendarSyncOutcome(calendar.Id, false, "authentication_required"));
                }
                catch (Exception exception) when (exception is HttpRequestException or FormatException)
                {
                    outcomes.Add(new CalendarSyncOutcome(calendar.Id, false, "remote_unavailable"));
                }
            });

        return outcomes.OrderBy(item => item.CalendarId, StringComparer.Ordinal).ToArray();
    }
}
