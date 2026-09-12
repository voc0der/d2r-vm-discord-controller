using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The join hold gives the leader a half-minute alone in a new game before the fleet raises its
// density. These pin the three things that make it safe to leave in the UI: it is off unless
// someone asks for it, it never survives the run that armed it, and it never delays a client
// rejoining a game the fleet is already in.
public sealed class FollowAutoJoinDelayTests
{
    [Fact]
    public void TheHoldIsOffUntilSomebodyArmsIt()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);

        Assert.False(control.JoinDelayArmed);
    }

    [Fact]
    public void TheButtonTogglesTheHoldBothWays()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);

        var armed = control.TryToggleJoinDelay();

        Assert.Equal(FollowAutoJoinDelayChangeOutcome.Changed, armed.Outcome);
        Assert.True(armed.Armed);
        Assert.True(control.JoinDelayArmed);

        var disarmed = control.TryToggleJoinDelay();

        Assert.Equal(FollowAutoJoinDelayChangeOutcome.Changed, disarmed.Outcome);
        Assert.False(disarmed.Armed);
        Assert.False(control.JoinDelayArmed);
    }

    // The whole point of not persisting it. Reset is what every run start calls - including the
    // resume after a host recovery - so a hold armed for one night's session is gone by the next
    // one rather than quietly slowing down a fleet nobody asked to slow down.
    [Fact]
    public void StartingARunClearsAHoldLeftArmedByTheLastOne()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);
        control.TryToggleJoinDelay();

        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);

        Assert.False(control.JoinDelayArmed);
    }

    // Public mode does not hold, so leaving the flag set while public owns the count would mean the
    // hold silently came back the moment someone pressed Private again - a control reactivating
    // itself without a press.
    [Fact]
    public void HandingTheCountToPublicModeClearsTheHold()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);
        control.TryToggleJoinDelay();

        control.TrySetMode(FollowAutoPartyMode.Public);

        Assert.False(control.JoinDelayArmed);
    }

    // A press that lands while public mode owns the count is refused rather than stored: the
    // monitor stops rendering the button in public mode, but the message ID is unchanged across
    // that edit, so a client still showing the pre-switch components can land one here.
    [Fact]
    public void PublicModeRefusesTheToggleInsteadOfStoringIt()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount, FollowAutoPartyMode.Public);

        var change = control.TryToggleJoinDelay();

        Assert.Equal(FollowAutoJoinDelayChangeOutcome.NotPrivateMode, change.Outcome);
        Assert.False(control.JoinDelayArmed);
    }

    // Same barrier the bot count and party mode sit behind: once local recovery is armed the run's
    // state has been journaled, and a press accepted after that snapshot would vanish when the host
    // came back.
    [Fact]
    public void AnArmedLocalRestartFreezesTheToggle()
    {
        var control = new FollowAutoTargetControl();
        control.Reset(FollowAutoRosterPolicy.DefaultBotCount);
        control.ArmLocalRestart();

        var change = control.TryToggleJoinDelay();

        Assert.Equal(FollowAutoJoinDelayChangeOutcome.LocalRestartArmed, change.Outcome);
        Assert.False(control.JoinDelayArmed);
    }

    [Fact]
    public void ThirtySecondsIsEnoughToReachEitherBoss()
    {
        Assert.Equal(30, FollowAutoJoinDelayPolicy.DelaySeconds);
    }

    [Fact]
    public void AFreshGameHoldsOnlyWhenPrivateModeArmedIt()
    {
        Assert.True(FollowAutoJoinDelayPolicy.ShouldHoldBeforeJoin(
            armed: true, FollowAutoPartyMode.Private, fleetClientsInGame: 0));
        Assert.False(FollowAutoJoinDelayPolicy.ShouldHoldBeforeJoin(
            armed: false, FollowAutoPartyMode.Private, fleetClientsInGame: 0));
        Assert.False(FollowAutoJoinDelayPolicy.ShouldHoldBeforeJoin(
            armed: true, FollowAutoPartyMode.Public, fleetClientsInGame: 0));
    }

    // The straggler case: a bot rejoining after a wedge or a resync joins a game the fleet is
    // already sitting in. The density is already raised and the leader is already past the run that
    // needed covering, so holding that client back protects nobody and just lengthens the window
    // where the fleet is short a vantage.
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void AClientRejoiningAGameTheFleetIsAlreadyInIsNeverHeld(int fleetClientsInGame)
    {
        Assert.False(FollowAutoJoinDelayPolicy.ShouldHoldBeforeJoin(
            armed: true, FollowAutoPartyMode.Private, fleetClientsInGame));
    }
}
