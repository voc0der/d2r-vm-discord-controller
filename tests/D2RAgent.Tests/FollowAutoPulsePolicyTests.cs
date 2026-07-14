using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoPulsePolicyTests
{
    // The public-game chaos this policy exists to fix: a stranger leaving used to be the only
    // leave signal, so the count drop triggered a mass leave and follow-auto immediately
    // rejoined everyone into the same game. With the leader verified present, a count drop
    // must rebaseline instead of leaving.
    [Fact]
    public void CountDropWithLeaderPresentRebaselinesInsteadOfLeaving()
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: true,
            playerCount: 5,
            baseline: 7,
            priorLeaderMissingStreak: 0);

        Assert.Equal(FollowAutoPulseAction.RebaselineAndWait, decision.Action);
        Assert.Equal(0, decision.LeaderMissingStreak);
    }

    [Fact]
    public void LeaderMissingOnceAsksForConfirmationBeforeLeaving()
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: false,
            playerCount: 5,
            baseline: 5,
            priorLeaderMissingStreak: 0);

        Assert.Equal(FollowAutoPulseAction.ConfirmLeaderGone, decision.Action);
        Assert.Equal(1, decision.LeaderMissingStreak);
    }

    [Fact]
    public void LeaderMissingTwiceLeaves()
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: false,
            playerCount: 5,
            baseline: 5,
            priorLeaderMissingStreak: 1);

        Assert.Equal(FollowAutoPulseAction.Leave, decision.Action);
        Assert.Equal(2, decision.LeaderMissingStreak);
    }

    // Even if the player count never changed (someone joined as the leader left), the leader
    // being gone is what ends the game for us.
    [Fact]
    public void LeaderGoneLeavesEvenWithoutACountDrop()
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: false,
            playerCount: 8,
            baseline: 8,
            priorLeaderMissingStreak: 1);

        Assert.Equal(FollowAutoPulseAction.Leave, decision.Action);
    }

    [Fact]
    public void LeaderReappearingResetsTheMissingStreak()
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: true,
            playerCount: 5,
            baseline: 5,
            priorLeaderMissingStreak: 1);

        Assert.Equal(FollowAutoPulseAction.RebaselineAndWait, decision.Action);
        Assert.Equal(0, decision.LeaderMissingStreak);
    }

    // "Couldn't check" (loading screen, sample failure, incomparable template) is not evidence
    // of absence: fall back to count-drop semantics and reset the streak.
    [Theory]
    [InlineData(4, 5, FollowAutoPulseAction.Leave)]
    [InlineData(5, 5, FollowAutoPulseAction.Wait)]
    [InlineData(null, 5, FollowAutoPulseAction.Wait)]
    [InlineData(5, null, FollowAutoPulseAction.Wait)]
    public void UnverifiableLeaderFallsBackToCountDrop(int? playerCount, int? baseline, FollowAutoPulseAction expected)
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: null,
            playerCount,
            baseline,
            priorLeaderMissingStreak: 1);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(0, decision.LeaderMissingStreak);
    }

    [Theory]
    [InlineData(4, 5, FollowAutoPulseAction.Leave)]
    [InlineData(5, 5, FollowAutoPulseAction.Wait)]
    [InlineData(6, 5, FollowAutoPulseAction.Wait)]
    [InlineData(null, null, FollowAutoPulseAction.Wait)]
    public void NoLeaderBoundKeepsTheOriginalCountDropBehavior(int? playerCount, int? baseline, FollowAutoPulseAction expected)
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: false,
            leaderPresent: null,
            playerCount,
            baseline,
            priorLeaderMissingStreak: 0);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(0, decision.LeaderMissingStreak);
    }
}
