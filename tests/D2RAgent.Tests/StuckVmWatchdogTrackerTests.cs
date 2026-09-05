using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The tracker owns the clock the watchdog acts on, so every bug here is a bug that power-cycles a
// healthy VM or never recovers a dead one.
public sealed class StuckVmWatchdogTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    // The sweep passes agentOfflineGraceSeconds here: staying connected for as long as the silence
    // that would have condemned the guest is what proves its recovery worked.
    private static readonly TimeSpan StableWindow = TimeSpan.FromMinutes(4);

    [Fact]
    public void TheFirstObservationOfASilenceStartsTheClockAtZero()
    {
        var tracker = new StuckVmWatchdogTracker();

        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc1", Start));
    }

    [Fact]
    public void AContinuousSilenceAccumulates()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);

        Assert.Equal(
            TimeSpan.FromMinutes(7),
            tracker.RecordOffline("hc1", Start + TimeSpan.FromMinutes(7)));
    }

    // The streak has to be continuous. An agent that flaps - offline, back, offline again - has not
    // been stuck the whole time, and treating it that way would cycle a VM that is merely unstable.
    [Fact]
    public void AnAgentComingBackRestartsTheClockImmediately()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordOnline("hc1", Start + TimeSpan.FromMinutes(1), StableWindow);

        Assert.Equal(
            TimeSpan.Zero,
            tracker.RecordOffline("hc1", Start + TimeSpan.FromHours(4)));
    }

    // A guest that stayed up proved the recovery worked. Holding its spent attempts against it
    // forever would leave it one cycle short the next time it wedges, weeks later.
    [Fact]
    public void AnAgentThatStaysConnectedRefundsItsRecoveryBudget()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);
        Assert.Equal(1, tracker.RecoveriesUsed("hc1"));

        tracker.RecordOnline("hc1", Start + TimeSpan.FromMinutes(1), StableWindow);
        tracker.RecordOnline("hc1", Start + TimeSpan.FromMinutes(1) + StableWindow, StableWindow);

        Assert.Equal(0, tracker.RecoveriesUsed("hc1"));
    }

    // The case that makes maxRecoveriesPerVm enforceable at all. A guest whose agent starts,
    // connects, and dies again would otherwise reclaim its whole allowance on every brief
    // appearance - so the watchdog would power-cycle it every few minutes forever and the dead-end
    // notice, the one thing that tells an operator to go look, could never fire.
    [Fact]
    public void ABriefAppearanceDoesNotRefundTheBudget()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);

        tracker.RecordOnline("hc1", Start + TimeSpan.FromMinutes(1), StableWindow);
        tracker.RecordOnline(
            "hc1",
            Start + TimeSpan.FromMinutes(1) + StableWindow - TimeSpan.FromSeconds(1),
            StableWindow);

        Assert.Equal(1, tracker.RecoveriesUsed("hc1"));
    }

    // The online run has to be continuous too, or a guest that flaps across many sweeps would
    // accumulate its way to a refund it never earned in one stretch.
    [Fact]
    public void AnOnlineRunBrokenBySilenceStartsOverForRefundPurposes()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);

        var flapAt = Start + TimeSpan.FromMinutes(1);
        tracker.RecordOnline("hc1", flapAt, StableWindow);
        tracker.RecordOffline("hc1", flapAt + TimeSpan.FromSeconds(30));
        tracker.RecordOnline("hc1", flapAt + TimeSpan.FromMinutes(1), StableWindow);

        // Well past StableWindow measured from the FIRST sighting, but only a minute into the
        // second run, so the budget stays spent.
        tracker.RecordOnline("hc1", flapAt + StableWindow, StableWindow);

        Assert.Equal(1, tracker.RecoveriesUsed("hc1"));
    }

    // An agent that was never recorded offline has nothing to refund and must not be tracked at
    // all - the sweep calls this for every online account on every tick.
    [Fact]
    public void AnAgentThatWasNeverMissingIsNotTracked()
    {
        var tracker = new StuckVmWatchdogTracker();

        tracker.RecordOnline("hc1", Start, StableWindow);

        Assert.Equal(0, tracker.RecoveriesUsed("hc1"));
        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc1", Start + TimeSpan.FromHours(4)));
    }

    // Charging the attempt also has to restart the clock. Otherwise the sweep a minute later still
    // sees an hour of accumulated silence and fires again while the VM is mid-boot.
    [Fact]
    public void ChargingAnAttemptRestartsTheClockSoTheNextSweepDoesNotImmediatelyRefire()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordOffline("hc1", Start + TimeSpan.FromHours(1));

        tracker.RecordRecoveryAttempt("hc1", Start + TimeSpan.FromHours(1));

        Assert.Equal(
            TimeSpan.FromMinutes(1),
            tracker.RecordOffline("hc1", Start + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AccountsAreTrackedIndependently()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);

        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc2", Start + TimeSpan.FromHours(2)));
        Assert.Equal(0, tracker.RecoveriesUsed("hc2"));
    }

    [Fact]
    public void TheGiveUpNoticeIsClaimedOnce()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);

        Assert.True(tracker.TryClaimGiveUpNotice("hc1"));
        Assert.False(tracker.TryClaimGiveUpNotice("hc1"));
    }

    // The single most important case in this file. On resume the wall clock has jumped by however
    // long the machine was suspended, and every guest was suspended along with it - so none of that
    // elapsed time is evidence. Without the reset the first post-resume sweep reads the whole fleet
    // as stuck for hours and power-cycles all of it at once.
    [Fact]
    public void AHostResumeClearsEveryStreakSoTheFleetIsNotCycledAtOnce()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordOffline("hc2", Start);

        tracker.Reset();

        var wokeAt = Start + TimeSpan.FromHours(9);
        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc1", wokeAt));
        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc2", wokeAt));
    }

    // A sweep that could not observe the guest at all - its owning worker node was unreachable, or
    // Hyper-V would not answer - has learned nothing about it. Every VM agent on a worker reaches
    // the master through that worker's process, so a worker self-update takes all of them offline
    // while their guests keep running; counting that would cycle a whole node's healthy VMs the
    // moment the worker came back.
    [Fact]
    public void SilenceTheHostCouldNotObserveIsNotCounted()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordOffline("hc1", Start + TimeSpan.FromMinutes(20));

        tracker.RecordUnobserved("hc1");

        Assert.Equal(
            TimeSpan.Zero,
            tracker.RecordOffline("hc1", Start + TimeSpan.FromMinutes(21)));
    }

    // Unlike an agent coming back, an unobservable sweep proves nothing about the guest, so it must
    // not hand back attempts already spent on it.
    [Fact]
    public void AnUnobservableSweepDoesNotRefundTheRecoveryBudget()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);

        tracker.RecordUnobserved("hc1");

        Assert.Equal(1, tracker.RecoveriesUsed("hc1"));
    }

    // Charging the attempt restarts the clock, but a recovery runs for as long as half an hour - an
    // in-place restart plus a full stop/start, each waiting out its own reconnect budget. Without a
    // second restart when it returns, the streak is already past the grace window and the next
    // sweep spends the rest of the budget on a guest that has been booting for one minute.
    [Fact]
    public void TheClockRestartsAgainWhenALongRecoveryFinishes()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);
        tracker.RecordRecoveryAttempt("hc1", Start);

        var recoveryEnded = Start + TimeSpan.FromMinutes(26);
        tracker.RestartClock("hc1", recoveryEnded);

        Assert.Equal(
            TimeSpan.FromMinutes(1),
            tracker.RecordOffline("hc1", recoveryEnded + TimeSpan.FromMinutes(1)));
    }

    // A clock that steps backwards (an NTP correction landing mid-streak) must not produce a
    // negative age that reads as "not stuck yet" for however long the correction was.
    [Fact]
    public void AClockThatStepsBackwardsDoesNotProduceANegativeAge()
    {
        var tracker = new StuckVmWatchdogTracker();
        tracker.RecordOffline("hc1", Start);

        Assert.Equal(TimeSpan.Zero, tracker.RecordOffline("hc1", Start - TimeSpan.FromMinutes(5)));
    }
}
