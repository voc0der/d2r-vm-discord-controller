using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class MenuReadyPolicyTests
{
    [Fact]
    public void ReadyFirstDoesNotRunForDisconnectedAgents()
    {
        Assert.False(MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson(
            connected: false,
            statusJson: """{"d2rRunning":false}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"battleNetRunning":true}""")]
    [InlineData("""{"d2rRunning":false,"d2rActivityState":"Unknown"}""")]
    [InlineData("""{"d2rRunning":true}""")]
    [InlineData("""{"d2rRunning":true,"d2rActivityState":"Unknown"}""")]
    public void ReadyFirstRunsWhenD2RIsNotKnownMenuReady(string? statusJson)
    {
        Assert.True(MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson(
            connected: true,
            statusJson));
    }

    [Theory]
    [InlineData("CharacterScreenIdle")]
    [InlineData("LobbyOrGame")]
    public void ReadyFirstSkipsOnlyKnownMenuReadyStates(string activityState)
    {
        var statusJson = $$"""{"d2rRunning":true,"d2rActivityState":"{{activityState}}"}""";

        Assert.False(MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson(
            connected: true,
            statusJson));
    }
}
