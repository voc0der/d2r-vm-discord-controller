using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Private mode fills the leader's game to whatever count the operator picked. Public mode makes the
// count an output instead: the fleet trims itself until exactly one slot is still free, so another
// real player can always walk in. These pin the arithmetic, the sample hygiene that guards it, and
// the mode switch itself.
public sealed class FollowAutoPublicModeTests
{
    [Fact]
    public void PublicModeHoldsTheGameOneSlotShortOfTheCap()
    {
        Assert.Equal(1, FollowAutoPublicModePolicy.ReservedSlots);
        Assert.Equal(
            FollowAutoRosterPolicy.MaxPlayersPerGame - 1,
            FollowAutoPublicModePolicy.TargetPlayerCount);
    }

    // The leader being followed is a real player and always holds one of the target's slots, so
    // public mode can never want a seventh bot however the target got there.
    [Fact]
    public void PublicModesCeilingIsOneBelowPrivateModes()
    {
        Assert.Equal(FollowAutoRosterPolicy.MaxBotCount - 1, FollowAutoPublicModePolicy.MaxBotCount);
        Assert.Equal(
            FollowAutoPublicModePolicy.MaxBotCount,
            FollowAutoPublicModePolicy.ClampTarget(FollowAutoRosterPolicy.MaxBotCount));
    }

    [Theory]
    // The leader alone: six bots, seven players, one seat free.
    [InlineData(1, 6)]
    [InlineData(2, 5)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(5, 2)]
    [InlineData(6, 1)]
    public void EveryRealPlayerThatArrivesCostsTheFleetOneBot(int humans, int expectedBots)
    {
        Assert.Equal(expectedBots, FollowAutoPublicModePolicy.ResolveTargetBotCount(humans));
        Assert.Equal(
            FollowAutoPublicModePolicy.TargetPlayerCount,
            humans + expectedBots);
    }

    // The one case where the promise cannot be kept, and it is a hard technical limit rather than a
    // policy choice: everything the run knows about the leader's game is read off a client inside
    // it. Drop to zero and follow-auto is blind - it cannot see the count, the bound nametag, or
    // the game ending, and nothing would ever bring it back.
    [Fact]
    public void ASeventhRealPlayerStillLeavesOneClientInAsTheFleetsOnlyVantage()
    {
        Assert.Equal(1, FollowAutoPublicModePolicy.ResolveTargetBotCount(7));
        Assert.Equal(1, FollowAutoPublicModePolicy.ResolveTargetBotCount(8));
        Assert.True(FollowAutoPublicModePolicy.IsHoldingLastVantage(1, humanCount: 7));
        // Six humans and one bot is seven players - the gap is genuinely held there.
        Assert.False(FollowAutoPublicModePolicy.IsHoldingLastVantage(1, humanCount: 6));
        Assert.False(FollowAutoPublicModePolicy.IsHoldingLastVantage(2, humanCount: 7));
    }

    // The sampling client counts itself in the party bar, so the fleet's own clients come straight
    // back off the total.
    [Theory]
    [InlineData(7, 6, 1)]
    [InlineData(7, 2, 5)]
    [InlineData(8, 1, 7)]
    [InlineData(2, 1, 1)]
    public void HumansAreWhatIsLeftOfThePartyAfterTheFleet(int playerCount, int bots, int expected)
    {
        Assert.Equal(expected, FollowAutoPublicModePolicy.CountHumans(playerCount, fresh: true, inGame: true, bots));
    }

    // A vantage that could not read a count, is reporting a cached one, or is sitting at the menus
    // says nothing about the current game. Acting on any of those would move real clients.
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(0, true, true)]
    [InlineData(7, false, true)]
    [InlineData(7, true, false)]
    public void AnUnusableSampleSaysNothingAboutTheGame(int? playerCount, bool fresh, bool inGame)
    {
        Assert.Null(FollowAutoPublicModePolicy.CountHumans(playerCount, fresh, inGame, botsInGame: 3));
    }

    // A joined count larger than the party bar (a bot that dropped without the host noticing) must
    // not read as negative humans and swing the target to the ceiling.
    [Fact]
    public void AJoinedCountAheadOfThePartyBarNeverGoesNegative()
    {
        Assert.Equal(0, FollowAutoPublicModePolicy.CountHumans(3, fresh: true, inGame: true, botsInGame: 6));
        Assert.Equal(
            FollowAutoPublicModePolicy.MaxBotCount,
            FollowAutoPublicModePolicy.ResolveTargetBotCount(0));
    }

    // One degraded capture that under-reads the party bar looks exactly like humans leaving, so a
    // single sample must never move a client.
    [Fact]
    public void OneSampleIsNotEnoughToClaimASlotBack()
    {
        var tracker = new FollowAutoPublicModeTracker();

        // Two bots in a game that reads five players: three humans, so four bots are wanted.
        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        Assert.Equal(4, Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
    }

    // Yielding early costs one bot a game; claiming early costs a real player the seat the whole
    // mode exists to protect. So the fleet gives ground faster than it takes it.
    [Fact]
    public void TheFleetGivesASlotBackFasterThanItTakesOne()
    {
        Assert.True(
            FollowAutoPublicModePolicy.ConfirmationsToYield
                < FollowAutoPublicModePolicy.ConfirmationsToClaim);

        var tracker = new FollowAutoPublicModeTracker();

        // Six bots in an eight-player game: two humans arrived, so five bots are wanted.
        Assert.Null(Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
        Assert.Equal(5, Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
    }

    [Fact]
    public void ADisagreeingSampleRestartsTheStreak()
    {
        var tracker = new FollowAutoPublicModeTracker();

        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        // A different reading lands: three bots wanted, not four, and the count starts over.
        Assert.Null(Observe(tracker, playerCount: 6, botsInGame: 2, currentTarget: 2));
        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        Assert.Null(Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
        Assert.Equal(4, Observe(tracker, playerCount: 5, botsInGame: 2, currentTarget: 2));
    }

    // An unusable sample should slow the decision down, not permanently block it: a fleet with one
    // flaky vantage still has to be able to give a seat back.
    [Fact]
    public void AnUnusableSampleNeitherAdvancesNorResetsTheStreak()
    {
        var tracker = new FollowAutoPublicModeTracker();

        Assert.Null(Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
        Assert.Null(tracker.Observe(null, fresh: true, inGame: true, botsInGame: 6, currentTarget: 6));
        Assert.Null(tracker.Observe(8, fresh: false, inGame: true, botsInGame: 6, currentTarget: 6));
        Assert.Equal(5, Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
    }

    // The run applies the target and the next pulse agrees with it, so a settled game keeps
    // offering nothing rather than re-proposing a change every heartbeat.
    [Fact]
    public void ASettledGameProposesNothing()
    {
        var tracker = new FollowAutoPublicModeTracker();

        for (var pulse = 0; pulse < 5; pulse++)
        {
            Assert.Null(Observe(tracker, playerCount: 7, botsInGame: 4, currentTarget: 4));
        }

        Assert.Equal(3, tracker.LastHumanCount);
        Assert.Equal(7, tracker.LastPlayerCount);
    }

    // The run can refuse an applied change (the shared adjustment throttle, an armed host restart).
    // Re-offering it on the next pulse is what makes that a delay instead of a lost decision.
    [Fact]
    public void ARefusedChangeIsOfferedAgainOnTheNextPulse()
    {
        var tracker = new FollowAutoPublicModeTracker();

        Assert.Null(Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
        Assert.Equal(5, Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
        Assert.Equal(5, Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
    }

    // The streak belongs to one game. The target does not: it is the best prior available for the
    // leader's next game, and re-deriving it from scratch would trickle the fleet back in one
    // client at a time on every single game.
    [Fact]
    public void LeavingAGameClearsTheReadingButNotTheDecision()
    {
        var tracker = new FollowAutoPublicModeTracker();
        Assert.Null(Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));

        tracker.Reset();

        Assert.Null(tracker.LastHumanCount);
        Assert.Null(tracker.LastPlayerCount);
        Assert.Null(Observe(tracker, playerCount: 8, botsInGame: 6, currentTarget: 6));
    }

    // Switching modes is also a target change, which is why both live in one lock: public mode's
    // ceiling is lower, and a full-game target has to be trimmed at the press rather than at the
    // first pulse.
    [Fact]
    public void SwitchingToPublicTrimsAFullGameImmediately()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);

        var change = control.TrySetMode(FollowAutoPartyMode.Public);

        Assert.Equal(FollowAutoPartyModeChangeOutcome.Changed, change.Outcome);
        Assert.Equal(FollowAutoPartyMode.Private, change.PreviousMode);
        Assert.Equal(FollowAutoRosterPolicy.DefaultBotCount, change.PreviousTarget);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, change.Target);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, control.TargetBotCount);
        Assert.Equal(FollowAutoPartyMode.Public, control.Mode);
    }

    [Fact]
    public void SwitchingBackToPrivateKeepsTheCountTheFleetSettledOn()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(3, FollowAutoPartyMode.Public);

        var change = control.TrySetMode(FollowAutoPartyMode.Private);

        Assert.Equal(FollowAutoPartyModeChangeOutcome.Changed, change.Outcome);
        Assert.Equal(3, change.Target);
        Assert.Equal(FollowAutoPartyMode.Private, control.Mode);
        // ...and the manual buttons reach the full game again from there.
        Assert.Equal(
            FollowAutoTargetAdjustmentOutcome.Changed,
            control.TryAdjust(4, _ => true).Outcome);
        Assert.Equal(FollowAutoRosterPolicy.MaxBotCount, control.TargetBotCount);
    }

    // While public mode owns the target, +1 must not be able to reach past public's ceiling - a
    // stale monitor render is enough to get a press through to the control.
    [Fact]
    public void PublicModesCeilingAlsoBindsTheManualAdjustment()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoPublicModePolicy.MaxBotCount, FollowAutoPartyMode.Public);

        var adjustment = control.TryAdjust(1, _ => true);

        Assert.Equal(FollowAutoTargetAdjustmentOutcome.AtLimit, adjustment.Outcome);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, control.TargetBotCount);
    }

    [Fact]
    public void PressingTheSameModeTwiceChangesNothing()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(4);

        var change = control.TrySetMode(FollowAutoPartyMode.Private);

        Assert.Equal(FollowAutoPartyModeChangeOutcome.Unchanged, change.Outcome);
        Assert.Equal(4, control.TargetBotCount);
    }

    // Same freeze the bot count gets: once the restart journal has the snapshot, a press that
    // arrives afterwards must be rejected rather than acknowledged and lost.
    [Fact]
    public void AnArmedHostRestartFreezesTheModeToo()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoPublicModePolicy.MaxBotCount, FollowAutoPartyMode.Public);

        var persisted = control.ArmLocalRestart();
        var change = control.TrySetMode(FollowAutoPartyMode.Private);

        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, persisted.TargetBotCount);
        Assert.Equal(FollowAutoPartyMode.Public, persisted.Mode);
        Assert.Equal(FollowAutoPartyModeChangeOutcome.LocalRestartArmed, change.Outcome);
        Assert.Equal(FollowAutoPartyMode.Public, control.Mode);
    }

    [Fact]
    public void ANewRunStartsInTheModeItWasGiven()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount, FollowAutoPartyMode.Public);

        // The requested count is trimmed to what public mode can run, not silently kept.
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, control.TargetBotCount);
        Assert.Equal(FollowAutoPartyMode.Public, control.Mode);

        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);

        Assert.Equal(FollowAutoRosterPolicy.DefaultBotCount, control.TargetBotCount);
        Assert.Equal(FollowAutoPartyMode.Private, control.Mode);
    }

    // The run loop reads the mode, decides, and writes across three separate operations, on a
    // different task from the gateway. A Private press landing in that window must win: otherwise
    // it is acknowledged and then immediately overwritten by the derived target it was meant to
    // stop.
    [Fact]
    public void ADerivedTargetNeverLandsOnTopOfASwitchBackToPrivate()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoPublicModePolicy.MaxBotCount, FollowAutoPartyMode.Public);

        // The gateway switches back while the run loop is still holding its decision.
        control.TrySetMode(FollowAutoPartyMode.Private);
        var adjustment = control.TrySetTargetForMode(2, FollowAutoPartyMode.Public);

        Assert.Equal(FollowAutoTargetAdjustmentOutcome.Refused, adjustment.Outcome);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, control.TargetBotCount);
    }

    [Fact]
    public void ADerivedTargetIsAppliedWhilePublicModeStillHoldsIt()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoPublicModePolicy.MaxBotCount, FollowAutoPartyMode.Public);

        var adjustment = control.TrySetTargetForMode(2, FollowAutoPartyMode.Public);

        Assert.Equal(FollowAutoTargetAdjustmentOutcome.Changed, adjustment.Outcome);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, adjustment.PreviousTarget);
        Assert.Equal(2, control.TargetBotCount);
    }

    [Fact]
    public void ADerivedTargetIsFrozenByAnArmedHostRestartToo()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(4, FollowAutoPartyMode.Public);
        control.ArmLocalRestart();

        var adjustment = control.TrySetTargetForMode(2, FollowAutoPartyMode.Public);

        Assert.Equal(FollowAutoTargetAdjustmentOutcome.LocalRestartArmed, adjustment.Outcome);
        Assert.Equal(4, control.TargetBotCount);
    }

    // Public mode can only ever ask for a count inside its own range, however it got there.
    [Fact]
    public void ADerivedTargetIsClampedToPublicModesRange()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(3, FollowAutoPartyMode.Public);

        Assert.Equal(
            FollowAutoTargetAdjustmentOutcome.Changed,
            control.TrySetTargetForMode(99, FollowAutoPartyMode.Public).Outcome);
        Assert.Equal(FollowAutoPublicModePolicy.MaxBotCount, control.TargetBotCount);

        Assert.Equal(
            FollowAutoTargetAdjustmentOutcome.Changed,
            control.TrySetTargetForMode(0, FollowAutoPartyMode.Public).Outcome);
        Assert.Equal(FollowAutoPublicModePolicy.MinBotCount, control.TargetBotCount);
    }

    // The full arc the feature is for: the leader's game fills up with real players and the fleet
    // gets out of the way, then empties and the fleet comes back - never taking the last free seat.
    [Fact]
    public void TheFleetTracksAGameAsItFillsAndEmpties()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount, FollowAutoPartyMode.Public);
        var tracker = new FollowAutoPublicModeTracker();

        // Leader alone with six bots: seven players, one seat free.
        Assert.Equal(6, control.TargetBotCount);
        Assert.Equal(6, Settle(control, tracker, humans: 1));

        // Four more real players arrive one at a time; each costs the fleet a bot.
        Assert.Equal(5, Settle(control, tracker, humans: 2));
        Assert.Equal(4, Settle(control, tracker, humans: 3));
        Assert.Equal(3, Settle(control, tracker, humans: 4));
        Assert.Equal(2, Settle(control, tracker, humans: 5));

        // Then they drift off and the fleet fills the room back in behind them.
        Assert.Equal(4, Settle(control, tracker, humans: 3));
        Assert.Equal(6, Settle(control, tracker, humans: 1));
    }

    // A Save and Exit the run gave up on leaves a client sitting in the game that the run has
    // stopped tracking. Public mode measures how full the game is by subtracting its own clients
    // from the party bar, so an untracked one reads as one more real player - and the fleet would
    // yield it a slot, then another, emptying the roster a step at a time against a game that
    // never actually gets any less full.
    [Fact]
    public void AClientTheRunGaveUpOnRemovingIsStillCountedAsOccupyingTheGame()
    {
        var state = new FollowAutoAccountState();
        state.MarkJoined("hc1");
        state.MarkJoined("hc2");
        state.MarkJoined("hc3");

        // hc3 was benched but never managed to leave; the run releases it to keep moving.
        state.Bench(new HashSet<string>(["hc3"], StringComparer.OrdinalIgnoreCase));
        state.MarkStrandedInGame("hc3");

        Assert.Equal(2, state.JoinedCount);
        Assert.Equal(1, state.StrandedInGameCount);
        // A seven-player party still holds three fleet clients, so four of those players are real.
        Assert.Equal(
            4,
            FollowAutoPublicModePolicy.CountHumans(
                7,
                fresh: true,
                inGame: true,
                botsInGame: state.JoinedCount + state.StrandedInGameCount));
        // Counting only the two the run is still tracking invents a fifth human, and public mode
        // would give away a slot to it.
        Assert.Equal(
            5,
            FollowAutoPublicModePolicy.CountHumans(7, fresh: true, inGame: true, state.JoinedCount));
    }

    [Fact]
    public void AStrandedClientThatGetsBackUnderControlIsNotCountedTwice()
    {
        var state = new FollowAutoAccountState();
        state.MarkStrandedInGame("hc3");

        state.MarkJoined("hc3");

        Assert.Equal(1, state.JoinedCount);
        Assert.Equal(0, state.StrandedInGameCount);
    }

    // The next game is a fresh situation, and a client left behind in the abandoned one is picked
    // up by the normal menu recovery path the next time it is rostered.
    [Fact]
    public void MovingToTheNextGameForgetsStrandedClients()
    {
        var state = new FollowAutoAccountState();
        state.MarkStrandedInGame("hc3");

        state.ClearStrandedInGame();

        Assert.Equal(0, state.StrandedInGameCount);
    }

    // Drives the tracker with the party bar those humans and the fleet's current target would
    // actually produce, then applies whatever it decides - the run loop's job, without the gateway.
    private static int Settle(
        FollowAutoTargetControl control,
        FollowAutoPublicModeTracker tracker,
        int humans)
    {
        for (var pulse = 0; pulse < 8; pulse++)
        {
            var bots = control.TargetBotCount;
            var decision = tracker.Observe(
                humans + bots,
                fresh: true,
                inGame: true,
                botsInGame: bots,
                currentTarget: bots);
            if (decision is { } target)
            {
                control.TryAdjust(target - bots, _ => true);
            }
        }

        return control.TargetBotCount;
    }

    private static int? Observe(
        FollowAutoPublicModeTracker tracker,
        int playerCount,
        int botsInGame,
        int currentTarget)
    {
        return tracker.Observe(playerCount, fresh: true, inGame: true, botsInGame, currentTarget);
    }
}
