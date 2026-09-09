using AgentCommon;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The vm_console reply has to survive the round trip through the worker transport, and every way it
// can fail has to mean "learned nothing" rather than "the guest is fine" - the caller clears the
// boot-logo clock on null, so an unreadable console can only ever delay a power cut, never cause
// one. That asymmetry is the safety property these pin.
public sealed class VmConsoleFrameReadingTests
{
    private static string FramePayload(int width, int height, byte fill)
    {
        var pixels = new byte[width * height * 2];
        Array.Fill(pixels, fill);
        return $$"""
            {"Width":{{width}},"Height":{{height}},"Base64":"{{Convert.ToBase64String(pixels)}}","Error":null}
            """;
    }

    [Fact]
    public void AWellFormedFrameIsDecodedToItsDimensions()
    {
        var stats = DiscordBot.TryReadVmConsoleFrame(
            CommandResult.Success(FramePayload(64, 48, 0x00)));

        Assert.NotNull(stats);
        Assert.Equal(64, stats!.Width);
        Assert.Equal(48, stats.Height);
        // All-zero RGB565 is pure black, which is near-black everywhere and cyan nowhere.
        Assert.Equal(1.0, stats.NearBlackRatio);
        Assert.Equal(0.0, stats.LogoCyanRatio);
    }

    [Fact]
    public void AFailedCommandYieldsNoFrameRatherThanThrowing()
    {
        Assert.Null(DiscordBot.TryReadVmConsoleFrame(CommandResult.Failure("Unsupported worker command")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"Width\":64,\"Height\":48}")]
    [InlineData("{\"Width\":64,\"Height\":48,\"Base64\":\"!!!not base64!!!\"}")]
    // The capture failed on the node and said so in the payload, which is the shape the PowerShell
    // returns rather than erroring - a VM mid-transition, or a host that cannot reach WMI.
    [InlineData("{\"Width\":256,\"Height\":192,\"Base64\":null,\"Error\":\"VM not found in WMI\"}")]
    // Short buffer: fewer bytes than the stated dimensions need, which must be rejected rather
    // than read as a smaller frame.
    [InlineData("{\"Width\":256,\"Height\":192,\"Base64\":\"AAAA\"}")]
    public void EveryMalformedReplyIsNoFrameRatherThanAnException(string payload)
    {
        Assert.Null(DiscordBot.TryReadVmConsoleFrame(CommandResult.Success(payload)));
    }
}
