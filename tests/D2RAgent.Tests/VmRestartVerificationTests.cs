using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// A Restart-VM that returns without erroring is not a guest that restarted. The cmdlet is routed
// through the same integration services a wedged guest has already stopped answering, so it can
// succeed having done nothing - and the watchdog picks this rung precisely when the guest still
// looks answerable, which is when that mistake is easiest to make.
//
// Before the host checked, the only evidence either way was whether the agent came back, so a
// restart that never happened and a restart that happened and did not help were the same outcome:
// six minutes of waiting, then escalation. These pin the reading that tells them apart.
public sealed class VmRestartVerificationTests
{
    [Fact]
    public void AnUptimeResetIsWhatProvesTheGuestRestarted()
    {
        Assert.Equal(
            DiscordBot.VmRestartObservation.Restarted,
            DiscordBot.ClassifyRestartOutcome(sawAnyUptime: true, uptimeReset: true));
    }

    // The case the whole check exists for: Hyper-V answered every poll, the guest kept counting up
    // from the same boot, so the restart demonstrably did not take.
    [Fact]
    public void UptimeThatNeverResetsIsReportedAsNotRestarted()
    {
        Assert.Equal(
            DiscordBot.VmRestartObservation.DidNotRestart,
            DiscordBot.ClassifyRestartOutcome(sawAnyUptime: true, uptimeReset: false));
    }

    // The safety property. Silence is the host's own blindness - the owning node stopped answering,
    // or Hyper-V would not report - and escalating a guest to a power cycle on the strength of that
    // would turn a diagnostic gap into an outage. Not being able to prove a restart happened is not
    // evidence that it did not.
    [Fact]
    public void NoUptimeReadingAtAllIsUnverifiableRatherThanAFailedRestart()
    {
        Assert.Equal(
            DiscordBot.VmRestartObservation.Unverifiable,
            DiscordBot.ClassifyRestartOutcome(sawAnyUptime: false, uptimeReset: false));
    }

    // Guards the direction of the comparison, which is the easiest thing here to invert: a guest
    // that restarted comes back with LESS uptime than it had, never more.
    [Theory]
    [InlineData(3600, 12, true)]
    [InlineData(240, 239, true)]
    [InlineData(240, 240, false)]
    [InlineData(240, 900, false)]
    public void OnlyASmallerUptimeCountsAsAReset(int beforeSeconds, int afterSeconds, bool restarted)
    {
        var uptimeReset = TimeSpan.FromSeconds(afterSeconds) < TimeSpan.FromSeconds(beforeSeconds);
        Assert.Equal(restarted, uptimeReset);

        Assert.Equal(
            restarted
                ? DiscordBot.VmRestartObservation.Restarted
                : DiscordBot.VmRestartObservation.DidNotRestart,
            DiscordBot.ClassifyRestartOutcome(sawAnyUptime: true, uptimeReset));
    }
}
