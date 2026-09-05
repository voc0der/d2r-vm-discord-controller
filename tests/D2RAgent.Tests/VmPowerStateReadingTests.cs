using AgentCommon;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The VM power cycle only counts as recovery if Hyper-V confirms Off and then Running, so the
// state has to survive the round trip through vm_status. ConvertTo-Json emits VMState as a number
// (and a worker node running an older build still does), so both shapes must read.
public sealed class VmPowerStateReadingTests
{
    [Theory]
    [InlineData("""{"Name":"d2r-hc-01","State":3}""", "Off")]
    [InlineData("""{"Name":"d2r-hc-01","State":2}""", "Running")]
    [InlineData("""{"Name":"d2r-hc-01","State":"Off"}""", "Off")]
    [InlineData("""{"Name":"d2r-hc-01","State":"Running"}""", "Running")]
    [InlineData("""[{"Name":"d2r-hc-01","State":3}]""", "Off")]
    public void ReadsTheVmStateFromEitherSerialization(string output, string expected)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.True(DiscordBot.TryReadVmPowerState(status, out var state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("""{"Name":"d2r-hc-01"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void UnreadableOutputIsNotAConfirmedState(string output)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.False(DiscordBot.TryReadVmPowerState(status, out _));
    }

    [Fact]
    public void ResultWithoutDataIsNotAConfirmedState()
    {
        Assert.False(DiscordBot.TryReadVmPowerState(CommandResult.Success("no data"), out _));
    }

    // The heartbeat field decides whether a guest that will not come back gets its power cut, so
    // reading it wrong is the difference between clearing a wedged boot and killing a live Windows.
    [Theory]
    [InlineData("""{"Name":"d2r-hc-01","State":2,"Heartbeat":"OK"}""", VmHeartbeatStatus.Ok)]
    [InlineData("""{"Name":"d2r-hc-01","State":2,"Heartbeat":"No Contact"}""", VmHeartbeatStatus.NoContact)]
    [InlineData("""{"Name":"d2r-hc-01","State":2,"Heartbeat":"Lost Communication"}""", VmHeartbeatStatus.NoContact)]
    [InlineData("""[{"Name":"d2r-hc-01","State":2,"Heartbeat":"OK"}]""", VmHeartbeatStatus.Ok)]
    public void ReadsTheHeartbeatWhenTheNodeReportsIt(string output, VmHeartbeatStatus expected)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.Equal(expected, DiscordBot.TryReadVmHeartbeat(status));
    }

    // Every one of these is a node that cannot tell us about the guest: an older worker that does
    // not send the field, a VM with the integration service switched off (null), or an unreadable
    // reply. None may read as a hang - Unknown makes the policy wait out its full patient window
    // rather than acting, so an un-updated worker degrades to the old behavior instead of a wrong
    // power cut.
    [Theory]
    [InlineData("""{"Name":"d2r-hc-01","State":2}""")]
    [InlineData("""{"Name":"d2r-hc-01","State":2,"Heartbeat":null}""")]
    [InlineData("""{"Name":"d2r-hc-01","State":2,"Heartbeat":""}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void AMissingOrUnreadableHeartbeatIsUnknownRatherThanAHang(string output)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.Equal(VmHeartbeatStatus.Unknown, DiscordBot.TryReadVmHeartbeat(status));
    }

    [Fact]
    public void ResultWithoutDataHasNoHeartbeat()
    {
        Assert.Equal(VmHeartbeatStatus.Unknown, DiscordBot.TryReadVmHeartbeat(CommandResult.Success("no data")));
    }

    // Uptime is what stops the stuck-VM watchdog from acting on a guest that is legitimately still
    // booting - the case that would otherwise cycle the whole fleet at once right after a resume.
    // ConvertTo-Json expands a TimeSpan into its properties rather than emitting a string, so Ticks
    // is the shape to expect; the rest are accepted because a worker node can be on a different
    // PowerShell major version than the master.
    [Theory]
    [InlineData("""{"State":2,"Uptime":{"Ticks":3000000000,"TotalSeconds":300.0}}""", 300)]
    [InlineData("""{"State":2,"Uptime":{"TotalSeconds":300.0}}""", 300)]
    [InlineData("""{"State":2,"Uptime":3000000000}""", 300)]
    [InlineData("""{"State":2,"Uptime":"00:05:00"}""", 300)]
    [InlineData("""[{"State":2,"Uptime":{"Ticks":3000000000}}]""", 300)]
    public void ReadsTheUptimeFromEverySerializationANodeMightSend(string output, int expectedSeconds)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DiscordBot.TryReadVmUptime(status));
    }

    // Every one of these is a node that did not tell us how long the VM has been up. None may read
    // as zero: zero is "it just booted", which would suppress recovery on that guest forever. The
    // policy treats null as missing evidence and falls through to its other guards instead.
    [Theory]
    [InlineData("""{"State":2}""")]
    [InlineData("""{"State":2,"Uptime":null}""")]
    [InlineData("""{"State":2,"Uptime":{"Days":0}}""")]
    [InlineData("""{"State":2,"Uptime":"not a timespan"}""")]
    [InlineData("not json")]
    public void AnUnreportedUptimeIsNullRatherThanZero(string output)
    {
        var status = CommandResult.Success("d2r-hc-01: ok", new { output });

        Assert.Null(DiscordBot.TryReadVmUptime(status));
    }

    [Fact]
    public void ResultWithoutDataHasNoUptime()
    {
        Assert.Null(DiscordBot.TryReadVmUptime(CommandResult.Success("no data")));
    }
}
