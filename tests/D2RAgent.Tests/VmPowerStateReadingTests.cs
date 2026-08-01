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
}
