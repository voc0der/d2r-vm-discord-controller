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
}
