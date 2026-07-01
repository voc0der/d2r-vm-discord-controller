using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class PlayerCountDropPollPolicyTests
{
    [Theory]
    [InlineData(1, 6)]
    [InlineData(5, 12)]
    [InlineData(10, 15)]
    [InlineData(20, 15)]
    public void DelayScalesWithAverageGameLength(int averageMinutes, int expectedSeconds)
    {
        var delay = PlayerCountDropPollPolicy.GetDelay(TimeSpan.FromMinutes(averageMinutes));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void UnknownOrFirstGameUsesDefaultMaxDelay()
    {
        var now = DateTimeOffset.Parse("2026-07-01T16:00:00Z");

        Assert.Equal(
            TimeSpan.FromSeconds(15),
            PlayerCountDropPollPolicy.GetDelay(autoStartedUtc: null, currentGameNumber: 15, now));
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            PlayerCountDropPollPolicy.GetDelay(now - TimeSpan.FromMinutes(1), currentGameNumber: 1, now));
    }

    [Fact]
    public void FifteenthGameAfterFifteenMinutesPollsFast()
    {
        var now = DateTimeOffset.Parse("2026-07-01T16:00:00Z");
        var started = now - TimeSpan.FromMinutes(15);

        var delay = PlayerCountDropPollPolicy.GetDelay(started, currentGameNumber: 15, now);

        Assert.Equal(TimeSpan.FromSeconds(6), delay);
    }
}
