using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// The live failure this guards (2026-07-22): the Battle.net launcher wedged at "Connecting...",
// so every broken-session D2R restart relaunched into the same dead session ("Cannot Connect to
// Server" forever). The second consecutive recovery of one incident must escalate to killing
// Battle.net.exe, not keep restarting only D2R.
public sealed class BrokenSessionRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstRecoveryOfAnIncidentRestartsOnlyD2R()
    {
        var decision = VmOperations.ClassifyBrokenSessionRecovery(lastRecoveryUtc: null, recoveryStreak: 0, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.RestartD2R, decision.Action);
        Assert.Equal(1, decision.RecoveryStreak);
    }

    [Fact]
    public void RecoveryInsideTheCooldownSkipsAndKeepsTheStreak()
    {
        var decision = VmOperations.ClassifyBrokenSessionRecovery(Now.AddSeconds(-30), recoveryStreak: 1, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.SkipRecentRestart, decision.Action);
        Assert.Equal(1, decision.RecoveryStreak);
    }

    [Fact]
    public void RepeatRecoveryAfterAFailedD2RRestartEscalatesToBattleNet()
    {
        var decision = VmOperations.ClassifyBrokenSessionRecovery(Now.AddMinutes(-5), recoveryStreak: 1, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.RestartBattleNetAndD2R, decision.Action);
        Assert.Equal(2, decision.RecoveryStreak);
    }

    [Fact]
    public void StillBrokenAfterAnEscalatedRestartKeepsEscalating()
    {
        var decision = VmOperations.ClassifyBrokenSessionRecovery(Now.AddMinutes(-5), recoveryStreak: 2, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.RestartBattleNetAndD2R, decision.Action);
        Assert.Equal(3, decision.RecoveryStreak);
    }

    [Fact]
    public void RecoveryOutsideTheEscalationWindowStartsAFreshIncident()
    {
        var decision = VmOperations.ClassifyBrokenSessionRecovery(Now.AddMinutes(-45), recoveryStreak: 3, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.RestartD2R, decision.Action);
        Assert.Equal(1, decision.RecoveryStreak);
    }

    [Fact]
    public void HealthySessionResetMakesTheNextRecoveryAPlainD2RRestart()
    {
        // Streak 0 models MarkBattleNetSessionHealthy having run after the last recovery:
        // even a prompt new incident starts over with the cheap restart.
        var decision = VmOperations.ClassifyBrokenSessionRecovery(Now.AddMinutes(-5), recoveryStreak: 0, Now);

        Assert.Equal(VmOperations.BrokenSessionRecoveryAction.RestartD2R, decision.Action);
        Assert.Equal(1, decision.RecoveryStreak);
    }
}
