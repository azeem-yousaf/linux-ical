using ICloudCalendar.Web.Services;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SyncWakeSignalTests
{
    [Fact]
    public async Task WakeInterruptsLongIdleWait()
    {
        using var signal = new SyncWakeSignal();
        signal.Wake();

        var wait = signal.WaitAsync(TimeSpan.FromHours(1), CancellationToken.None);

        wait.IsCompletedSuccessfully.ShouldBeTrue();
        await wait;
    }

    [Fact]
    public async Task RepeatedWakeEventsCoalesceWithoutThrowing()
    {
        using var signal = new SyncWakeSignal();

        signal.Wake();
        signal.Wake();
        signal.Wake();

        await signal.WaitAsync(TimeSpan.FromHours(1), CancellationToken.None);
    }
}
