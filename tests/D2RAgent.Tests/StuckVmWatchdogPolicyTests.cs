using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Pins the decision to power-cycle a guest nobody asked about. The trap this policy exists to avoid
// is treating Hyper-V's power state as health: a hung VM reports "Running" forever, so most of what
// is asserted here is that the verdict comes from the agent's silence and the heartbeat's
// corroboration, and never from the state alone.
public sealed class StuckVmWatchdogPolicyTests
{
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan NoEvidenceGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumUptime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LongUptime = TimeSpan.FromHours(2);

    private static StuckVmWatchdogPolicy Policy(bool enabled = true, int maxRecoveries = 2)
    {
        return new StuckVmWatchdogPolicy(new StuckVmWatchdogOptions(
            enabled,
            OfflineGrace,
            NoEvidenceGrace,
            MinimumUptime,
            maxRecoveries));
    }

    private static VmWatchdogAssessment Assess(
        StuckVmWatchdogPolicy policy,
        string? powerState = "Running",
        VmHeartbeatStatus heartbeat = VmHeartbeatStatus.Ok,
        TimeSpan? uptime = null,
        TimeSpan? offlineFor = null,
        int recoveriesUsed = 0)
    {
        return policy.Assess(
            agentOnline: false,
            powerState,
            heartbeat,
            uptime ?? LongUptime,
            offlineFor ?? OfflineGrace,
            recoveriesUsed);
    }

    // A connected agent is the only proof that matters, and it outranks every other reading -
    // including a heartbeat that has gone quiet on a guest which is plainly working.
    [Theory]
    [InlineData(VmHeartbeatStatus.Ok)]
    [InlineData(VmHeartbeatStatus.NoContact)]
    [InlineData(VmHeartbeatStatus.Unknown)]
    public void AConnectedAgentIsHealthyWhateverElseIsReported(VmHeartbeatStatus heartbeat)
    {
        var assessment = Policy().Assess(
            agentOnline: true,
            "Running",
            heartbeat,
            LongUptime,
            TimeSpan.FromHours(3),
            recoveriesUsed: 0);

        Assert.Equal(VmWatchdogVerdict.Healthy, assessment.Verdict);
    }

    // The point of the whole policy. Hyper-V reaching the guest OS while our agent is gone rules
    // out "still booting" as the explanation, which makes it the strongest evidence available -
    // and it is exactly the case VmHangRecoveryPolicy refuses to act on, so if this returned
    // KeepWaiting nothing anywhere would ever recover the guest.
    //
    // It asks for a restart rather than a power cycle because the guest is still answering its
    // integration services, so the in-place path is available - and that path never turns the VM
    // off, so there is no Off state for a failed start to strand it in.
    [Fact]
    public void AReachableGuestThatIsNotRunningTheAgentIsRestartedInPlace()
    {
        var assessment = Assess(Policy(), heartbeat: VmHeartbeatStatus.Ok);

        Assert.Equal(VmWatchdogVerdict.Restart, assessment.Verdict);
        Assert.Contains("not a slow boot", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // The mirror of the case above, and the reason the verdict is not always Restart: Restart-VM is
    // routed through the very integration services this guest has stopped answering, so asking it
    // to restart itself would be asking the part that is broken. Only the full cycle, which can
    // escalate to a hard cut, can help here.
    [Fact]
    public void AGuestTheHypervisorCannotReachGoesStraightToTheFullCycle()
    {
        var assessment = Assess(Policy(), heartbeat: VmHeartbeatStatus.NoContact);

        Assert.Equal(VmWatchdogVerdict.PowerCycle, assessment.Verdict);
    }

    // No heartbeat reported means no reason to believe the cooperative path will be answered.
    [Fact]
    public void AnUnreportedHeartbeatDoesNotEarnTheCooperativePath()
    {
        var assessment = Assess(
            Policy(),
            heartbeat: VmHeartbeatStatus.Unknown,
            offlineFor: NoEvidenceGrace);

        Assert.Equal(VmWatchdogVerdict.PowerCycle, assessment.Verdict);
    }

    // "Running" is what a hung VM, a healthy VM, and one frozen on the boot logo all report, so it
    // can never be the thing that makes a guest look fine. Only the agent's return does that.
    [Fact]
    public void RunningIsNotTreatedAsEvidenceOfHealth()
    {
        var assessment = Assess(Policy(), powerState: "Running", heartbeat: VmHeartbeatStatus.Ok);

        Assert.NotEqual(VmWatchdogVerdict.Healthy, assessment.Verdict);
    }

    // Anything that is not Running is either off on purpose or a transition someone else started.
    // Both are somebody's deliberate act, and starting a power cycle into one would race it.
    [Theory]
    [InlineData("Off")]
    [InlineData("Saved")]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Paused")]
    [InlineData(null)]
    public void AVmThatIsNotRunningIsLeftAlone(string? powerState)
    {
        var assessment = Assess(
            Policy(),
            powerState: powerState,
            offlineFor: TimeSpan.FromHours(6));

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, assessment.Verdict);
    }

    // The guard that matters most right after a host resume, when every VM in the fleet is booting
    // at once and none of them has an agent yet.
    [Fact]
    public void AVmThatOnlyJustPoweredOnIsStillBooting()
    {
        var assessment = Assess(
            Policy(),
            heartbeat: VmHeartbeatStatus.NoContact,
            uptime: MinimumUptime - TimeSpan.FromSeconds(1),
            offlineFor: TimeSpan.FromHours(1));

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, assessment.Verdict);
        Assert.Contains("boot", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAgentInsideItsGraceWindowIsNotYetStuck()
    {
        var assessment = Assess(Policy(), offlineFor: OfflineGrace - TimeSpan.FromSeconds(1));

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, assessment.Verdict);
    }

    // With the integration service off there is no corroboration at all, so the offline streak has
    // to carry the decision alone and waits out the longer window first.
    [Fact]
    public void AnUnreportedHeartbeatWaitsTheLongerWindow()
    {
        var policy = Policy();

        var early = Assess(
            policy,
            heartbeat: VmHeartbeatStatus.Unknown,
            offlineFor: NoEvidenceGrace - TimeSpan.FromSeconds(1));
        var late = Assess(
            policy,
            heartbeat: VmHeartbeatStatus.Unknown,
            offlineFor: NoEvidenceGrace);

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, early.Verdict);
        Assert.NotEqual(VmWatchdogVerdict.KeepWaiting, late.Verdict);
    }

    // An unknown uptime is a worker node that did not report one. That is missing evidence, not a
    // guest that just booted - reading it as zero would suppress every recovery forever.
    [Fact]
    public void AnUnreportedUptimeDoesNotBlockRecovery()
    {
        var assessment = Policy().Assess(
            agentOnline: false,
            "Running",
            VmHeartbeatStatus.Ok,
            vmUptime: null,
            OfflineGrace,
            recoveriesUsed: 0);

        Assert.Equal(VmWatchdogVerdict.Restart, assessment.Verdict);
    }

    // The streak is wall-clock silence, and a guest that was rebooted by something other than this
    // watchdog - a node restart, the host power lifecycle's resume, an operator's Start-VM - comes
    // back still carrying the silence from before that boot. Acting on it would leave
    // MinimumVmUptime as the only guard between a booting guest and a power cycle, when the value
    // documented as covering a cold boot is agentOfflineGraceSeconds.
    [Fact]
    public void SilenceFromBeforeTheGuestsCurrentBootDoesNotCount()
    {
        var assessment = Assess(
            Policy(),
            heartbeat: VmHeartbeatStatus.NoContact,
            uptime: MinimumUptime + TimeSpan.FromSeconds(30),
            offlineFor: TimeSpan.FromHours(3));

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, assessment.Verdict);
    }

    // The clamp must not become its own excuse for never acting: once the VM has been up longer
    // than the grace window, the streak carries the decision exactly as before.
    [Fact]
    public void AGuestUpLongerThanTheGraceWindowIsStillRecovered()
    {
        var assessment = Assess(
            Policy(),
            heartbeat: VmHeartbeatStatus.NoContact,
            uptime: OfflineGrace + TimeSpan.FromMinutes(1),
            offlineFor: TimeSpan.FromHours(3));

        Assert.Equal(VmWatchdogVerdict.PowerCycle, assessment.Verdict);
    }

    [Fact]
    public void TheRecoveryBudgetIsFinite()
    {
        var assessment = Assess(Policy(maxRecoveries: 2), recoveriesUsed: 2);

        Assert.Equal(VmWatchdogVerdict.GiveUp, assessment.Verdict);
    }

    [Fact]
    public void DisablingTheWatchdogStopsItActing()
    {
        var assessment = Assess(
            Policy(enabled: false),
            offlineFor: TimeSpan.FromHours(6));

        Assert.Equal(VmWatchdogVerdict.KeepWaiting, assessment.Verdict);
        Assert.Contains("disabled", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // The sweep costs a PowerShell process per probed VM, so it must not probe one that cannot
    // possibly be actionable yet.
    [Fact]
    public void ProbingWaitsForTheShortestWindowThatCouldAct()
    {
        Assert.Equal(OfflineGrace, Policy().ProbeAfter);
    }
}
