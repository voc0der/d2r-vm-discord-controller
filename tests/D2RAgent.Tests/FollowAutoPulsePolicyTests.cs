using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoPulsePolicyTests
{
    // The public-game chaos this policy exists to fix: a stranger leaving used to be the only
    // leave signal, so the count drop triggered a mass leave and follow-auto immediately
    // rejoined everyone into the same game. With the leader verified present, a count drop
    // just rebaselines.
    [Fact]
    public void LeaderPresentRebaselinesEvenWhenTheCountDropped()
    {
        Assert.Equal(
            FollowAutoPulseAction.RebaselineAndWait,
            FollowAutoPulsePolicy.Classify(leaderBound: true, leaderPresent: true, playerCount: 5, baseline: 7));
    }

    // A missing leader is only a FLAG here - the host forces a second VM to agree before
    // leaving. Classify's job is just to raise it.
    [Fact]
    public void LeaderMissingFlagsForConfirmation()
    {
        Assert.Equal(
            FollowAutoPulseAction.LeaderMissingHere,
            FollowAutoPulsePolicy.Classify(leaderBound: true, leaderPresent: false, playerCount: 5, baseline: 5));
    }

    // "Couldn't check" (loading screen, capture failure, incomparable template) is not evidence
    // of absence - it must never flag the leader missing. It falls through to count-drop
    // semantics instead.
    [Theory]
    [InlineData(4, 5, FollowAutoPulseAction.CountDropLeave)]
    [InlineData(5, 5, FollowAutoPulseAction.Wait)]
    [InlineData(null, 5, FollowAutoPulseAction.Wait)]
    [InlineData(5, null, FollowAutoPulseAction.Wait)]
    public void UnverifiableLeaderFallsBackToCountDrop(int? playerCount, int? baseline, FollowAutoPulseAction expected)
    {
        Assert.Equal(
            expected,
            FollowAutoPulsePolicy.Classify(leaderBound: true, leaderPresent: null, playerCount, baseline));
    }

    [Theory]
    [InlineData(4, 5, FollowAutoPulseAction.CountDropLeave)]
    [InlineData(5, 5, FollowAutoPulseAction.Wait)]
    [InlineData(6, 5, FollowAutoPulseAction.Wait)]
    [InlineData(null, null, FollowAutoPulseAction.Wait)]
    public void NoLeaderBoundKeepsTheOriginalCountDropBehavior(int? playerCount, int? baseline, FollowAutoPulseAction expected)
    {
        Assert.Equal(
            expected,
            FollowAutoPulsePolicy.Classify(leaderBound: false, leaderPresent: null, playerCount, baseline));
    }

    // The heartbeat halves the shared count-drop cadence (leaving promptly matters more than
    // sparing a screen grab), then divides by the online vantage count so each VM keeps roughly
    // its own load while the fleet samples faster - floored so the chatter stays sane, and
    // never divided for a lone VM.
    [Theory]
    [InlineData(15, 4, 1.875)] // 15 / 2 / 4
    [InlineData(12, 4, 1.5)] // 12 / 2 / 4
    [InlineData(6, 4, 1.0)] // 6 / 2 / 4 = 0.75, floored to MinHeartbeatSeconds
    [InlineData(6, 8, 1.0)] // 6 / 2 / 8 = 0.375, floored to MinHeartbeatSeconds
    [InlineData(15, 1, 7.5)] // single VM: halved only
    [InlineData(15, 0, 7.5)] // no online count known: treated as single
    public void HeartbeatHalvesBaseThenDividesByVantageCountWithAFloor(int baseSeconds, int vantages, double expectedSeconds)
    {
        var delay = FollowAutoPulsePolicy.GetHeartbeat(TimeSpan.FromSeconds(baseSeconds), vantages);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 9);
    }

    [Fact]
    public void HeartbeatNeverGoesBelowTheFloor()
    {
        Assert.Equal(
            FollowAutoPulsePolicy.MinHeartbeatSeconds,
            FollowAutoPulsePolicy.GetHeartbeat(TimeSpan.FromSeconds(1), 8).TotalSeconds,
            9);
    }
}
