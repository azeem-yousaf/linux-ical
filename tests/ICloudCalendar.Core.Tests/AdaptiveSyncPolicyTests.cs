using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AdaptiveSyncPolicyTests
{
    private readonly AdaptiveSyncPolicy _policy = new();

    [Fact]
    public void NextDelayPollsActiveUserWithinFifteenSeconds()
    {
        _policy.NextDelay(userIsActive: true, consecutiveFailures: 0)
            .ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void NextDelayReducesIdleNetworkAndBatteryUse()
    {
        _policy.NextDelay(userIsActive: false, consecutiveFailures: 0)
            .ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData(true, 1, 30)]
    [InlineData(true, 2, 60)]
    [InlineData(false, 1, 240)]
    public void NextDelayBacksOffAfterFailures(bool active, int failures, int seconds)
    {
        _policy.NextDelay(active, failures).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void NextDelayCapsBackoffAtFifteenMinutes()
    {
        _policy.NextDelay(userIsActive: false, consecutiveFailures: 20)
            .ShouldBe(TimeSpan.FromMinutes(15));
    }
}
