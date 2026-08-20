using ICloudCalendar.Core;

namespace ICloudCalendar.Web.Services;

public interface IUserActivityMonitor
{
    bool IsActive { get; }
    void RecordActivity();
}

public interface ISyncWakeSignal
{
    void Wake();
    Task WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken);
}

public sealed class SyncWakeSignal : ISyncWakeSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Multiple activity events coalesce into one immediate sync round.
        }
    }

    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken) =>
        _ = await _signal.WaitAsync(maximumDelay, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

public sealed class UserActivityMonitor(
    TimeProvider timeProvider,
    ISyncWakeSignal wakeSignal) : IUserActivityMonitor
{
    private long _lastActivityUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    public bool IsActive => timeProvider.GetUtcNow()
        - DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastActivityUnixMilliseconds))
        < TimeSpan.FromMinutes(5);

    public void RecordActivity()
    {
        Interlocked.Exchange(
            ref _lastActivityUnixMilliseconds,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        wakeSignal.Wake();
    }
}

public interface ISyncStatusReader
{
    IReadOnlyList<AccountSyncStatus> GetAll();
}

public sealed record AccountSyncStatus(
    string AccountId,
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    IReadOnlyList<CalendarSyncOutcome> Calendars);

public sealed class CalendarSyncBackgroundService(
    ICalendarSyncCoordinator coordinator,
    IUserActivityMonitor activity,
    ISyncWakeSignal wakeSignal,
    AdaptiveSyncPolicy policy,
    ILogger<CalendarSyncBackgroundService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogSyncRoundFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1001, "CalendarSyncRoundFailed"),
        "Calendar synchronization round failed.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var roundSucceeded = await coordinator.SyncAllAsync(stoppingToken);
                failures = roundSucceeded ? 0 : failures + 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failures++;
                LogSyncRoundFailed(logger, exception);
            }

            await wakeSignal.WaitAsync(policy.NextDelay(activity.IsActive, failures), stoppingToken);
        }
    }
}
