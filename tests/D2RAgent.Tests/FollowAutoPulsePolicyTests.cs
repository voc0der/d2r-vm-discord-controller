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
    // of absence: fall back to count-drop semantics. With round-robin vantages it must not
    // erase another vantage's evidence either, so the missing streak is preserved - a VM stuck
    // outside the game taking its turn between the flagging scan and the confirming scan can't
    // restart the confirmation.
    [Theory]
    [InlineData(4, 5, FollowAutoPulseAction.Leave)]
    [InlineData(5, 5, FollowAutoPulseAction.Wait)]
    [InlineData(null, 5, FollowAutoPulseAction.Wait)]
    [InlineData(5, null, FollowAutoPulseAction.Wait)]
    public void UnverifiableLeaderFallsBackToCountDropAndPreservesTheStreak(int? playerCount, int? baseline, FollowAutoPulseAction expected)
    {
        var decision = FollowAutoPulsePolicy.Decide(
            leaderBound: true,
            leaderPresent: null,
            playerCount,
            baseline,
            priorLeaderMissingStreak: 1);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(1, decision.LeaderMissingStreak);
    }

    [Fact]
    public void FlagThenBlindVantageThenConfirmStillLeaves()
    {
        // hc2 flags the leader missing, hc3 is stuck in a loading screen on its turn, hc4's
        // scan confirms - the blind turn must not have reset the streak.
        var flagged = FollowAutoPulsePolicy.Decide(true, false, 5, 5, 0);
        Assert.Equal(FollowAutoPulseAction.ConfirmLeaderGone, flagged.Action);

        var blind = FollowAutoPulsePolicy.Decide(true, null, null, 5, flagged.LeaderMissingStreak);
        Assert.Equal(FollowAutoPulseAction.Wait, blind.Action);
        Assert.Equal(flagged.LeaderMissingStreak, blind.LeaderMissingStreak);

        var confirmed = FollowAutoPulsePolicy.Decide(true, false, 5, 5, blind.LeaderMissingStreak);
        Assert.Equal(FollowAutoPulseAction.Leave, confirmed.Action);
    }

    // The rotated delay divides the base poll interval by the online vantage count (each VM
    // keeps its original per-VM cadence; the fleet samples N times faster), floored so the
    // host<->agent chatter stays sane, and degenerates to the base delay for a single VM.
    [Theory]
    [InlineData(15, 4, 3.75)]
    [InlineData(6, 4, 1.5)]
    [InlineData(6, 8, 1.0)] // 0.75s divided, floored to MinRotatedPollSeconds
    [InlineData(15, 1, 15.0)]
    [InlineData(15, 0, 15.0)]
    public void RotatedPollDelayDividesByVantageCountWithAFloor(int baseSeconds, int vantages, double expectedSeconds)
    {
        var delay = FollowAutoPulsePolicy.GetRotatedPollDelay(TimeSpan.FromSeconds(baseSeconds), vantages);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 9);
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
