using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AdaptiveSyncPolicyTests
{
    private readonly AdaptiveSyncPolicy _policy = new();

    [Fact]
    public void NextDelayPollsActiveUserEverySecond()
    {
        _policy.NextDelay(userIsActive: true, consecutiveFailures: 0)
            .ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NextDelayPollsIdleUserEverySecond()
    {
        _policy.NextDelay(userIsActive: false, consecutiveFailures: 0)
            .ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(true, 2, 4)]
    [InlineData(false, 1, 2)]
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
