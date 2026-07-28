using D2RHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace D2RAgent.Tests;

// Sleep was the one power action with no shutdown.exe equivalent, so it went through a direct
// SetSuspendState P/Invoke - which needs SE_SHUTDOWN_NAME enabled, something Windows does not do
// for you even in an elevated process. The call failed inside a queued background task whose only
// outlet was a log file, so Discord reported the sleep as queued and the machine stayed awake. On
// a headless host with the screen already off, that is indistinguishable from a working sleep.
public sealed class HostSleepTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void AnyLegacyStandbyStateUsesSetSuspendState(bool systemS1, bool systemS2, bool systemS3)
    {
        Assert.Equal(
            HostSystemOperations.SleepMechanism.LegacySuspend,
            HostSystemOperations.ResolveSleepMechanism(
                systemS1, systemS2, systemS3, systemS4: true, hiberFilePresent: true));
    }

    [Fact]
    public void NoLegacyStandbyFallsBackToHibernation()
    {
        Assert.Equal(
            HostSystemOperations.SleepMechanism.Hibernate,
            HostSystemOperations.ResolveSleepMechanism(
                systemS1: false,
                systemS2: false,
                systemS3: false,
                systemS4: true,
                hiberFilePresent: true));
    }

    // The Latitude 7430: "Standby (S0 Low Power Idle) Network Connected" is its only available
    // state, S1/S2/S3 are unsupported by firmware, and hibernation is switched off. SetSuspendState
    // drives the legacy suspend path, so it returned ERROR_NOT_SUPPORTED (50) there - from a SYSTEM
    // service and from an elevated interactive session alike. Counting Modern Standby as usable is
    // what let this machine pass preflight and then fail every single time.
    [Fact]
    public void ModernStandbyAloneIsNotReachableAndMustNotPassPreflight()
    {
        Assert.Equal(
            HostSystemOperations.SleepMechanism.None,
            HostSystemOperations.ResolveSleepMechanism(
                systemS1: false,
                systemS2: false,
                systemS3: false,
                systemS4: false,
                hiberFilePresent: false));
    }

    [Fact]
    public void HibernationSupportedButSwitchedOffIsNotUsable()
    {
        // "Hibernation has not been enabled" means no hiberfil, and shutdown /h would fail.
        Assert.Equal(
            HostSystemOperations.SleepMechanism.None,
            HostSystemOperations.ResolveSleepMechanism(
                systemS1: false,
                systemS2: false,
                systemS3: false,
                systemS4: true,
                hiberFilePresent: false));
    }

    [Fact]
    public void SleepPreparationReportsWhyItCannotSleep()
    {
        var operations = new HostSystemOperations(NullLogger<HostSystemOperations>.Instance);

        var prepared = operations.TryPrepareSleep(out var error);

        if (OperatingSystem.IsWindows())
        {
            // A developer machine holds the privilege; the point is that preparation is what
            // decides, not the queued task that answers long after the command has replied.
            Assert.True(prepared || !string.IsNullOrWhiteSpace(error));
        }
        else
        {
            Assert.False(prepared);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void SleepPreparationNeverSucceedsSilentlyOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var operations = new HostSystemOperations(NullLogger<HostSystemOperations>.Instance);

        Assert.False(operations.TryPrepareSleep(out var error));
        Assert.Contains("Windows", error, StringComparison.OrdinalIgnoreCase);
    }
}
