using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Pins the shapes of the VM power verbs, and above all that vm_reboot restarts the guest IN PLACE
// rather than being a stop followed by a start.
//
// Why that distinction is worth a test file. The stuck-VM watchdog tries a restart first on any
// guest whose integration services still answer, precisely because the VM keeps its virtual power
// across it: there is no Off state in between, so there is no moment where a failed Start-VM leaves
// the VM down. If someone ever "simplifies" vm_reboot into Stop-VM + Start-VM, every one of those
// properties is silently lost and the cheap rung of the ladder becomes as risky as the expensive
// one while still being reported as the cheap one.
//
// What this file does NOT claim: that a real Restart-VM revives a real wedged guest. That is a
// property of Hyper-V and of the guest, not of this code, and it cannot be observed from a unit
// test - the watchdog therefore never trusts the cmdlet's return value, and judges the restart only
// by whether the agent actually comes back (see RestartStuckVmAsync).
public sealed class VmRestartCommandTests
{
    private const string VmName = "d2r-hc-01";

    private static string Script(string command)
    {
        var script = HyperVOperations.TryBuildVmPowerScript(command, VmName);
        Assert.NotNull(script);
        return script!;
    }

    // The central assertion of the file.
    [Fact]
    public void ARebootIsOneRestartAndNotAStopFollowedByAStart()
    {
        var script = Script("vm_reboot");

        Assert.Contains("Restart-VM", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-VM", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-VM", script, StringComparison.Ordinal);
    }

    // -TurnOff is an uncontrolled power cut and must never leak into the verb the watchdog reaches
    // for first. A reboot that quietly cut power would take the guest's unflushed writes with it.
    [Fact]
    public void ARebootNeverCutsPower()
    {
        Assert.DoesNotContain("-TurnOff", Script("vm_reboot"), StringComparison.Ordinal);
    }

    // Each verb asks the guest for progressively less, so no two may collapse into the same script.
    [Fact]
    public void EveryPowerVerbIsADistinctCommand()
    {
        var scripts = new[] { "vm_start", "vm_stop", "vm_turnoff", "vm_reboot" }
            .Select(Script)
            .ToArray();

        Assert.Equal(scripts.Length, scripts.Distinct(StringComparer.Ordinal).Count());
    }

    // Stop-VM -Force still routes through the guest's integration services; only -TurnOff does not.
    // The hard cut has to stay the single verb that needs nothing from the guest.
    [Fact]
    public void OnlyTurnOffBypassesTheGuest()
    {
        Assert.DoesNotContain("-TurnOff", Script("vm_stop"), StringComparison.Ordinal);
        Assert.Contains("-TurnOff", Script("vm_turnoff"), StringComparison.Ordinal);
    }

    // Every verb ends by re-reading status in the same round trip, which is what lets a caller
    // confirm the post-action state without a second call to a node that may be slow or remote.
    [Theory]
    [InlineData("vm_status")]
    [InlineData("vm_start")]
    [InlineData("vm_stop")]
    [InlineData("vm_turnoff")]
    [InlineData("vm_reboot")]
    public void EveryVerbReportsTheResultingState(string command)
    {
        var script = Script(command);

        Assert.Contains("Get-VM", script, StringComparison.Ordinal);
        Assert.Contains("Heartbeat", script, StringComparison.Ordinal);
        Assert.Contains("Uptime", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandBuildsNothing()
    {
        Assert.Null(HyperVOperations.TryBuildVmPowerScript("vm_explode", VmName));
        Assert.Null(HyperVOperations.TryBuildVmPowerScript("vm_snapshot", VmName));
    }

    // A VM name is operator-supplied config that ends up inside a PowerShell string literal. The
    // allowlist in EnsureAllowedVmName is the real gate, but this is the layer under it: a name that
    // did get through must stay data. Doubling is how a single-quoted PowerShell literal escapes a
    // quote, so the payload's own quotes surviving as '' is exactly what proves it never closed the
    // literal and became a second statement.
    [Fact]
    public void AQuoteInAVmNameStaysInsideItsLiteral()
    {
        var script = HyperVOperations.TryBuildVmPowerScript("vm_reboot", "d2r'; Remove-VM -Name '*");

        Assert.NotNull(script);
        Assert.Contains("'d2r''; Remove-VM -Name ''*'", script!, StringComparison.Ordinal);
    }
}
