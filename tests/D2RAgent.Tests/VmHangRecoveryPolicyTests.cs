using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Pins the decision that cuts a VM's power without asking the guest. The operator's constraint on
// this feature was explicit - never assume a VM is hung - so most of what is asserted here is the
// cases where the policy must REFUSE, not the ones where it acts.
public sealed class VmHangRecoveryPolicyTests
{
    private static readonly TimeSpan HangSuspectedAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoEvidenceGrace = TimeSpan.FromMinutes(20);

    private static VmHangRecoveryPolicy Policy(bool enabled = true, int maxCuts = 2)
    {
        return new VmHangRecoveryPolicy(new VmHangRecoveryOptions(
            enabled,
            HangSuspectedAfter,
            NoEvidenceGrace,
            TimeSpan.FromSeconds(10),
            maxCuts));
    }

    // The single most important case in this file. Hyper-V's heartbeat answering means the guest
    // kernel is alive and scheduling, so the machine is NOT frozen on the boot logo - whatever is
    // keeping the agent away needs a different fix, and cutting power would destroy a live Windows
    // to no purpose. No elapsed time may override this.
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(600)]
    public void ALiveGuestIsNeverPowerCutNoMatterHowLongItHasBeenWaiting(int minutes)
    {
        var assessment = Policy().AssessBootHang(
            "Running",
            VmHeartbeatStatus.Ok,
            TimeSpan.FromMinutes(minutes),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
        Assert.Contains("heartbeat", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // A cold boot legitimately takes minutes and shows no heartbeat the whole time. Acting inside
    // the grace window would power-cut healthy VMs that were merely still starting.
    [Fact]
    public void NoHeartbeatInsideTheGraceWindowKeepsWaiting()
    {
        var assessment = Policy().AssessBootHang(
            "Running",
            VmHeartbeatStatus.NoContact,
            HangSuspectedAfter - TimeSpan.FromSeconds(1),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
    }

    [Fact]
    public void NoHeartbeatPastTheGraceWindowIsAWedgedBoot()
    {
        var assessment = Policy().AssessBootHang(
            "Running",
            VmHeartbeatStatus.NoContact,
            HangSuspectedAfter,
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.HardPowerCut, assessment.Verdict);
    }

    // An unreported heartbeat is absence of evidence, not evidence of a hang: the integration
    // service can be switched off per-VM, and older worker nodes do not send the field at all.
    // It must never act as fast as a real "no contact" reading.
    [Fact]
    public void AnUnreportedHeartbeatWaitsTheFullNoEvidenceWindowInsteadOfTheShortOne()
    {
        var policy = Policy();

        var atShortWindow = policy.AssessBootHang(
            "Running", VmHeartbeatStatus.Unknown, HangSuspectedAfter, hardPowerCutsUsed: 0);
        Assert.Equal(VmHangVerdict.KeepWaiting, atShortWindow.Verdict);

        var atFullWindow = policy.AssessBootHang(
            "Running", VmHeartbeatStatus.Unknown, NoEvidenceGrace, hardPowerCutsUsed: 0);
        Assert.Equal(VmHangVerdict.HardPowerCut, atFullWindow.Verdict);
    }

    // Any state other than Running means another transition is already in flight - a restore pass,
    // an operator, a stop that has not settled. Cutting power into that races it.
    [Theory]
    [InlineData("Off")]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Saved")]
    [InlineData(null)]
    public void OnlyARunningVmIsEverPowerCutDuringBoot(string? state)
    {
        var assessment = Policy().AssessBootHang(
            state,
            VmHeartbeatStatus.NoContact,
            TimeSpan.FromHours(1),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
    }

    [Fact]
    public void AttemptsAreBoundedSoAHopelessVmEscalatesInsteadOfLooping()
    {
        var policy = Policy(maxCuts: 2);

        Assert.Equal(
            VmHangVerdict.HardPowerCut,
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 1).Verdict);
        Assert.Equal(
            VmHangVerdict.GiveUp,
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 2).Verdict);
    }

    [Fact]
    public void DisablingTheFeatureRestoresThePreviousGiveUpBehavior()
    {
        var policy = Policy(enabled: false);

        Assert.Equal(
            VmHangVerdict.KeepWaiting,
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 0).Verdict);
        Assert.Equal(
            VmHangVerdict.GiveUp,
            policy.AssessStuckShutdown("Stopping", TimeSpan.FromMinutes(5), 0).Verdict);
    }

    // Stop-VM -Force is routed through the guest's integration services. A guest that never answers
    // it for the full timeout is, by Hyper-V's own report, not running them - there is no ambiguity
    // left to guard against here, unlike the boot case.
    [Theory]
    [InlineData("Stopping")]
    [InlineData("Running")]
    [InlineData("Saved")]
    public void AShutdownThatWentUnansweredIsCutWithoutNeedingAHeartbeat(string state)
    {
        var assessment = Policy().AssessStuckShutdown(state, TimeSpan.FromMinutes(3), hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.HardPowerCut, assessment.Verdict);
    }

    [Fact]
    public void AVmThatReachedOffNeedsNoPowerCut()
    {
        var assessment = Policy().AssessStuckShutdown("Off", TimeSpan.FromMinutes(3), hardPowerCutsUsed: 0);

        Assert.Equal(VmHangVerdict.KeepWaiting, assessment.Verdict);
    }

    // "Lost Communication" is the guest answering and then stopping - precisely the mid-restart
    // freeze this whole feature exists for - so it must count as a hang, not as an unknown.
    [Theory]
    [InlineData("OK", VmHeartbeatStatus.Ok)]
    [InlineData("ok", VmHeartbeatStatus.Ok)]
    [InlineData("No Contact", VmHeartbeatStatus.NoContact)]
    [InlineData("Lost Communication", VmHeartbeatStatus.NoContact)]
    [InlineData("Degraded", VmHeartbeatStatus.NoContact)]
    [InlineData("", VmHeartbeatStatus.Unknown)]
    [InlineData("   ", VmHeartbeatStatus.Unknown)]
    [InlineData(null, VmHeartbeatStatus.Unknown)]
    public void HeartbeatDescriptionsMapToTheThreeCasesThatMatter(string? description, VmHeartbeatStatus expected)
    {
        Assert.Equal(expected, VmHangRecoveryPolicy.ParseHeartbeat(description));
    }

    // Every verdict carries an explanation, because these end up in the follow-auto monitor where
    // an operator has to be able to see why a VM's power was or was not cut.
    [Fact]
    public void EveryAssessmentExplainsItself()
    {
        var policy = Policy();
        VmHangAssessment[] assessments =
        [
            policy.AssessBootHang("Running", VmHeartbeatStatus.Ok, TimeSpan.FromMinutes(30), 0),
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, TimeSpan.FromMinutes(1), 0),
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 0),
            policy.AssessBootHang("Running", VmHeartbeatStatus.Unknown, TimeSpan.FromMinutes(1), 0),
            policy.AssessBootHang("Off", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 0),
            policy.AssessBootHang("Running", VmHeartbeatStatus.NoContact, NoEvidenceGrace, 99),
            policy.AssessStuckShutdown("Stopping", TimeSpan.FromMinutes(3), 0),
            policy.AssessStuckShutdown("Off", TimeSpan.FromMinutes(3), 0),
        ];

        Assert.All(assessments, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
    }
}
