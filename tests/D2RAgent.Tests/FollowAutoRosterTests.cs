using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Follow-auto used to send every online account into every game - fleet size was party size. With
// more VMs than one player's game can hold, a run now carries a bot target instead, and the live
// monitor's -1 / +1 buttons change it mid-run. These pin who ends up in the game.
public sealed class FollowAutoRosterTests
{
    // The target counts BOTS. D2R caps a game at 8 and the leader being followed holds one of
    // those slots, so the default of 7 fills the game exactly and -1 makes it a 7-player game.
    [Fact]
    public void TheDefaultTargetFillsAGameAlongsideTheLeader()
    {
        Assert.Equal(7, FollowAutoRosterPolicy.DefaultBotCount);
        Assert.Equal(
            FollowAutoRosterPolicy.MaxPlayersPerGame,
            FollowAutoRosterPolicy.DefaultBotCount + 1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(7, 7)]
    [InlineData(8, 7)]
    [InlineData(99, 7)]
    public void TargetsAreClampedToWhatAGameCanHold(int requested, int expected)
    {
        Assert.Equal(expected, FollowAutoRosterPolicy.ClampTarget(requested));
    }

    [Fact]
    public void TheRosterTakesTheTargetAndBenchesTheRest()
    {
        var roster = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc3", "hc4", "hc5"],
            targetBotCount: 3);

        Assert.Equal(["hc1", "hc2", "hc3"], roster.Active);
        Assert.Equal(["hc4", "hc5"], roster.Benched);
    }

    [Fact]
    public void AFleetSmallerThanTheTargetJustUsesEveryone()
    {
        var roster = FollowAutoRosterPolicy.ResolveRoster(["hc1", "hc2"], targetBotCount: 7);

        Assert.Equal(["hc1", "hc2"], roster.Active);
        Assert.Empty(roster.Benched);
    }

    // A worker node that connects after the run started brings VMs that were never online before.
    // They have to be usable straight away - that is the whole point of adding capacity mid-session.
    [Fact]
    public void VmsThatComeOnlineMidRunFillOpenSlots()
    {
        var beforeWorkerConnected = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2"],
            targetBotCount: 4);
        Assert.Equal(["hc1", "hc2"], beforeWorkerConnected.Active);

        var afterWorkerConnected = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc7", "hc8"],
            targetBotCount: 4,
            incumbents: new HashSet<string>(["hc1", "hc2"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["hc1", "hc2", "hc7", "hc8"], afterWorkerConnected.Active);
        Assert.Empty(afterWorkerConnected.Benched);
    }

    // The scenario in full: a run starts while only 5 VMs are reachable, then a worker node
    // connects bringing 2 more. With the default target of 7, both newcomers must be pulled in -
    // this is why ClampTarget deliberately does not clamp to the online count. A target that
    // followed the outage down to 5 could never climb back, and the extra capacity would sit idle
    // for the rest of the session.
    [Fact]
    public void AWorkerNodeArrivingWithTwoMoreVmsFillsTheDefaultTarget()
    {
        var beforeWorker = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc3", "hc4", "hc5"],
            FollowAutoRosterPolicy.DefaultBotCount);
        Assert.Equal(5, beforeWorker.Active.Count);
        Assert.Empty(beforeWorker.Benched);

        var afterWorker = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc3", "hc4", "hc5", "hc6", "hc7"],
            FollowAutoRosterPolicy.DefaultBotCount,
            incumbents: new HashSet<string>(
                ["hc1", "hc2", "hc3", "hc4", "hc5"],
                StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["hc1", "hc2", "hc3", "hc4", "hc5", "hc6", "hc7"], afterWorker.Active);
        Assert.Empty(afterWorker.Benched);
    }

    // The other half of that scenario, and the reason the two are not the same: an operator who
    // explicitly asked for 5 bots keeps 5. The newcomers are benched, not drafted.
    [Fact]
    public void AnExplicitBotCountIsNotRaisedByNewCapacity()
    {
        var afterWorker = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc3", "hc4", "hc5", "hc6", "hc7"],
            targetBotCount: 5,
            incumbents: new HashSet<string>(
                ["hc1", "hc2", "hc3", "hc4", "hc5"],
                StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["hc1", "hc2", "hc3", "hc4", "hc5"], afterWorker.Active);
        Assert.Equal(["hc6", "hc7"], afterWorker.Benched);
        // ...and +1 is offered, because there is now a spare VM to promote.
        Assert.True(FollowAutoRosterPolicy.CanAddBot(5, onlineAccountCount: 7, livePlayerCount: 6));
    }

    // The dangerous version of the same event: the newcomer sorts EARLIER than a bot already in
    // the leader's game. Ordering purely by key would kick a live client to swap in an
    // alphabetically luckier one.
    [Fact]
    public void ALateArrivalNeverDisplacesABotAlreadyInTheGame()
    {
        var roster = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc4", "hc5"],
            targetBotCount: 2,
            incumbents: new HashSet<string>(["hc4", "hc5"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["hc4", "hc5"], roster.Active);
        Assert.Equal(["hc1"], roster.Benched);
    }

    [Fact]
    public void IncumbentsBeyondTheTargetAreStillBenched()
    {
        // Lowering the target has to bench someone even though every candidate is an incumbent.
        var roster = FollowAutoRosterPolicy.ResolveRoster(
            ["hc1", "hc2", "hc3"],
            targetBotCount: 2,
            incumbents: new HashSet<string>(["hc1", "hc2", "hc3"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["hc1", "hc2"], roster.Active);
        Assert.Equal(["hc3"], roster.Benched);
    }

    // A VM going offline must not shrink the target - it comes back, and a target that quietly
    // followed an outage down would never climb back on its own.
    [Fact]
    public void AnOfflineVmDoesNotLowerTheTarget()
    {
        var roster = FollowAutoRosterPolicy.ResolveRoster(["hc1", "hc2"], targetBotCount: 7);

        Assert.Equal(7, roster.TargetBotCount);
        Assert.Equal(2, roster.Active.Count);
    }

    [Theory]
    // Room in the game, a spare VM benched, count known and under the cap.
    [InlineData(3, 9, 5, true)]
    // Already at the cap for bots.
    [InlineData(7, 9, 4, false)]
    // No benched VM left to promote.
    [InlineData(4, 4, 5, false)]
    // The live game is full - a freed slot belongs to whoever left it, not to a waiting bot.
    [InlineData(3, 9, 8, false)]
    public void AddingABotNeedsRoomAVmAndAFreeSlot(
        int target,
        int onlineAccounts,
        int livePlayerCount,
        bool expected)
    {
        Assert.Equal(expected, FollowAutoRosterPolicy.CanAddBot(target, onlineAccounts, livePlayerCount));
    }

    // No fleet account could read a count (nobody in a game yet, or every sample degraded). With no
    // game to be full of, only the cap and the bench constrain growing the roster.
    [Fact]
    public void AnUnknownPlayerCountDoesNotBlockAddingABot()
    {
        Assert.True(FollowAutoRosterPolicy.CanAddBot(3, onlineAccountCount: 9, livePlayerCount: null));
    }

    [Fact]
    public void RemovingABotStopsAtOne()
    {
        Assert.True(FollowAutoRosterPolicy.CanRemoveBot(2));
        Assert.False(FollowAutoRosterPolicy.CanRemoveBot(FollowAutoRosterPolicy.MinBotCount));
    }

    // Each press moves a real client in or out of a live game and takes a cycle to land, so a
    // double-click has to be refused rather than queued.
    [Fact]
    public void TheAdjustmentGateRefusesRapidPresses()
    {
        var gate = new FollowAutoRosterAdjustmentGate();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(gate.TryAdjust(start, out _));
        Assert.False(gate.TryAdjust(start.AddSeconds(1), out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), FollowAutoRosterAdjustmentGate.MinimumInterval);
        Assert.True(gate.TryAdjust(start + FollowAutoRosterAdjustmentGate.MinimumInterval, out _));
    }

    // The sets are read inside per-account filters. A recomputed property there allocated a fresh
    // HashSet for every candidate it tested; this pins that one roster hands out one set.
    [Fact]
    public void RosterSetsAreBuiltOnce()
    {
        var roster = FollowAutoRosterPolicy.ResolveRoster(["hc1", "hc2", "hc3"], targetBotCount: 2);

        Assert.Same(roster.ActiveSet, roster.ActiveSet);
        Assert.Same(roster.BenchedSet, roster.BenchedSet);
        Assert.True(roster.ActiveSet.Contains("HC1"));
        Assert.True(roster.BenchedSet.Contains("HC3"));
    }

    // "a 8-player game" is the one that would show up constantly, since 7 bots is the default.
    [Theory]
    [InlineData(7, "an 8-player game")]
    [InlineData(6, "a 7-player game")]
    [InlineData(1, "a 2-player game")]
    public void PartySizeReadsCorrectly(int bots, string expected)
    {
        Assert.Equal(expected, DiscordBot.FormatPartySize(bots));
    }

    [Fact]
    public void TheAdjustmentGateStartsFreshForANewRun()
    {
        var gate = new FollowAutoRosterAdjustmentGate();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(gate.TryAdjust(start, out _));
        gate.Reset();

        Assert.True(gate.TryAdjust(start.AddSeconds(1), out _));
    }
}
