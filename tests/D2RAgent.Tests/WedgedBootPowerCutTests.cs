using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// A guest wedged on the Windows boot logo is the case every graceful path is wrong for: its agent
// never started, and Stop-VM -Force is a guest-cooperative shutdown routed through integration
// services the guest is not running. Only Stop-VM -TurnOff gets it back.
//
// The policy always knew this. What went wrong is that a wedged guest could not reach the policy:
// RecoverVmAsync issued the graceful stop first and returned outright if it failed, which is
// exactly what a guest in this state makes it do. These pin the readings that now route it
// straight to the cut instead.
public sealed class WedgedBootPowerCutTests
{
    private static VmHangRecoveryPolicy NewPolicy(bool enabled = true)
    {
        return new VmHangRecoveryPolicy(new VmHangRecoveryOptions(
            Enabled: enabled,
            HangSuspectedAfter: TimeSpan.FromMinutes(3),
            NoEvidenceGrace: TimeSpan.FromMinutes(10),
            SettleBeforeRestart: TimeSpan.FromSeconds(5),
            MaxHardPowerCuts: 2));
    }

    // The screenshot case: powered on, hypervisor has no contact with the guest OS, well past a
    // cold boot. Nothing inside the guest can be asked anything, so the plug pull is the answer.
    [Fact]
    public void ARunningGuestWithNoHeartbeatPastTheGraceIsCutRatherThanAsked()
    {
        var assessment = NewPolicy().AssessBootHang(
            VmHangRecoveryPolicy.RunningState,
            VmHeartbeatStatus.NoContact,
            TimeSpan.FromMinutes(20),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.HardPowerCut, assessment.Verdict);
    }

    // The guard that keeps this from ever being an assumption, and the reason a bare "logo on
    // screen for 10s" trigger would have been wrong: a guest whose heartbeat answers is up and
    // scheduling, so a power cut would be destroying a live machine to fix something it cannot fix.
    [Fact]
    public void ALiveGuestIsNeverCutNoMatterHowLongItsAgentHasBeenMissing()
    {
        var assessment = NewPolicy().AssessBootHang(
            VmHangRecoveryPolicy.RunningState,
            VmHeartbeatStatus.Ok,
            TimeSpan.FromHours(3),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
    }

    // Every healthy VM shows the boot logo for a while on every boot. The grace window is what
    // separates a wedged boot from an ordinary one, so inside it the answer is still "wait".
    [Fact]
    public void AGuestStillInsideItsBootGraceIsNotYetWedged()
    {
        var assessment = NewPolicy().AssessBootHang(
            VmHangRecoveryPolicy.RunningState,
            VmHeartbeatStatus.NoContact,
            TimeSpan.FromSeconds(30),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
    }

    // A stop that errors is the same guest-cooperative call failing for the same reason, so it is
    // evidence for the cut rather than a reason to abandon the recovery. Zero elapsed because the
    // cmdlet failed rather than timing out.
    [Fact]
    public void AStopThatFailsOutrightStillAuthorizesTheCut()
    {
        var assessment = NewPolicy().AssessStuckShutdown(
            VmHangRecoveryPolicy.RunningState,
            TimeSpan.Zero,
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.HardPowerCut, assessment.Verdict);
    }

    // Bounded, so a guest that will not come back escalates to a human instead of looping cuts.
    [Fact]
    public void CutsAreBoundedSoAGuestThatWillNotReturnReachesAPerson()
    {
        var assessment = NewPolicy().AssessBootHang(
            VmHangRecoveryPolicy.RunningState,
            VmHeartbeatStatus.NoContact,
            TimeSpan.FromMinutes(20),
            hardPowerCutsUsed: 2);

        Assert.Equal(VmHangVerdict.GiveUp, assessment.Verdict);
    }
}
