using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// vm_console is the only VM verb that is not a power action, and it runs on a schedule against
// guests the host is already suspicious of. If it ever grew a power cmdlet, the watchdog would be
// acting on every VM it merely looked at - so that is pinned here rather than left to review.
public sealed class VmConsoleCommandTests
{
    private static string Script()
    {
        var script = HyperVOperations.TryBuildVmPowerScript("vm_console", "D2R_6");
        Assert.NotNull(script);
        return script!;
    }

    [Theory]
    [InlineData("Stop-VM")]
    [InlineData("Start-VM")]
    [InlineData("Restart-VM")]
    [InlineData("-TurnOff")]
    [InlineData("Remove-VM")]
    [InlineData("Set-VM")]
    public void ReadingTheConsoleNeverTouchesTheGuestsPower(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Script(), StringComparison.OrdinalIgnoreCase);
    }

    // The capture has to come from the hypervisor rather than from anything inside the guest -
    // that is the entire reason it can answer for a guest whose agent never started.
    [Fact]
    public void TheCaptureComesFromTheHypervisorsThumbnailApi()
    {
        var script = Script();

        Assert.Contains("GetVirtualSystemThumbnailImage", script, StringComparison.Ordinal);
        Assert.Contains("Msvm_VirtualSystemManagementService", script, StringComparison.Ordinal);
    }

    // Errors come back inside the payload, so a host that cannot capture degrades to "no frame"
    // and the caller learns nothing, rather than the sweep seeing a failed command.
    [Fact]
    public void CaptureFailuresAreReportedInThePayloadRatherThanThrown()
    {
        var script = Script();

        Assert.Contains("catch", script, StringComparison.Ordinal);
        Assert.Contains("Error", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbIsStillRejected()
    {
        Assert.Null(HyperVOperations.TryBuildVmPowerScript("vm_screenshot", "D2R_6"));
    }
}
