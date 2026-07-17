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

    // Multi-alt rolodex lock: a nametag must be verifiably SEEN before it can drive anything.
    // Neither "absent" nor "couldn't check" is lockable, so an operator playing an unbound alt
    // leaves follow-auto safely on count-drop semantics all session.
    [Fact]
    public void NoLockWhenNothingIsVerifiablyPresent()
    {
        Assert.Null(FollowAutoPulsePolicy.PickNametagLockIndex(Array.Empty<FollowAutoPulsePolicy.LeaderNametagSignal>()));
        Assert.Null(FollowAutoPulsePolicy.PickNametagLockIndex(new[]
        {
            new FollowAutoPulsePolicy.LeaderNametagSignal(false, 0.4),
            new FollowAutoPulsePolicy.LeaderNametagSignal(null, 0.9)
        }));
    }

    [Fact]
    public void LockPicksTheOnlyPresentNametag()
    {
        Assert.Equal(1, FollowAutoPulsePolicy.PickNametagLockIndex(new[]
        {
            new FollowAutoPulsePolicy.LeaderNametagSignal(false, 0.5),
            new FollowAutoPulsePolicy.LeaderNametagSignal(true, 0.7),
            new FollowAutoPulsePolicy.LeaderNametagSignal(null, 0.0)
        }));
    }

    // Several present at once (prefix-squatting names, multiboxing): the strongest match wins,
    // not the first - a longer name containing a shorter bound one verbatim scores lower than
    // the exact name does against itself.
    [Fact]
    public void LockPrefersTheHighestScoreAmongPresentNametags()
    {
        Assert.Equal(2, FollowAutoPulsePolicy.PickNametagLockIndex(new[]
        {
            new FollowAutoPulsePolicy.LeaderNametagSignal(true, 0.70),
            new FollowAutoPulsePolicy.LeaderNametagSignal(false, 0.99),
            new FollowAutoPulsePolicy.LeaderNametagSignal(true, 0.85)
        }));
    }

    // Ties keep the earliest bind - the rolodex order the operator built.
    [Fact]
    public void LockBreaksScoreTiesByBindOrder()
    {
        Assert.Equal(0, FollowAutoPulsePolicy.PickNametagLockIndex(new[]
        {
            new FollowAutoPulsePolicy.LeaderNametagSignal(true, 0.8),
            new FollowAutoPulsePolicy.LeaderNametagSignal(true, 0.8)
        }));
    }

    // The count-drop baseline must climb to the highest count actually observed: a seed pulse
    // that raced the party still forming (or fell back to a cached pre-join count) would
    // otherwise pin the baseline below the real in-game count and make the leader's departure
    // invisible - the watch-follow-auto-20260717-105143.log failure shape.
    [Fact]
    public void BaselineRisesToTheHighestObservedCount()
    {
        Assert.Equal(5, FollowAutoPulsePolicy.RaiseCountBaseline(4, 5));
        Assert.Equal(5, FollowAutoPulsePolicy.RaiseCountBaseline(null, 5));
    }

    [Fact]
    public void BaselineNeverDropsAndSurvivesUnsampledPulses()
    {
        Assert.Equal(5, FollowAutoPulsePolicy.RaiseCountBaseline(5, 4));
        Assert.Equal(5, FollowAutoPulsePolicy.RaiseCountBaseline(5, null));
        Assert.Null(FollowAutoPulsePolicy.RaiseCountBaseline(null, null));
    }

    // Cross-VM confirmation: an independent "gone" is the two-screen agreement a leave
    // requires, and a third screen's "still present" (cross-matching template, diverged list)
    // must not veto it - a single such VM used to starve every leave for a whole session.
    [Fact]
    public void ConfirmationPrefersAnyVerifiedAbsence()
    {
        Assert.False(FollowAutoPulsePolicy.CombineLeaderConfirmations(new bool?[] { true, false, null }));
        Assert.False(FollowAutoPulsePolicy.CombineLeaderConfirmations(new bool?[] { false }));
    }

    [Fact]
    public void ConfirmationReadsPresentOnlyWhenNobodyVerifiedAbsence()
    {
        Assert.True(FollowAutoPulsePolicy.CombineLeaderConfirmations(new bool?[] { null, true }));
    }

    // All-null (mid-load, missing entries, old agents) stays "couldn't check" - never evidence
    // of absence, never evidence of presence.
    [Fact]
    public void ConfirmationStaysUnknownWhenNobodyCouldCheck()
    {
        Assert.Null(FollowAutoPulsePolicy.CombineLeaderConfirmations(new bool?[] { null, null }));
        Assert.Null(FollowAutoPulsePolicy.CombineLeaderConfirmations(Array.Empty<bool?>()));
    }
}
