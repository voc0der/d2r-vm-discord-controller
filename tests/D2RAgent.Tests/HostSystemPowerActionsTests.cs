using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class HostSystemPowerActionsTests
{
    [Theory]
    [InlineData("sleep", HostSystemPowerAction.Sleep)]
    [InlineData("shutdown", HostSystemPowerAction.Shutdown)]
    [InlineData("restart", HostSystemPowerAction.Restart)]
    public void ParsesD2RSystemSubcommands(string subcommand, HostSystemPowerAction expected)
    {
        Assert.Equal(expected, HostSystemPowerActions.ParseAction(subcommand));
    }

    [Theory]
    [InlineData(HostSystemPowerAction.Shutdown, "/s")]
    [InlineData(HostSystemPowerAction.Restart, "/r")]
    public void ShutdownActionsUseLocalWindowsShutdown(HostSystemPowerAction action, string expectedMode)
    {
        var startInfo = HostSystemPowerActions.CreateShutdownStartInfo(action);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("shutdown.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Contains(expectedMode, arguments);
        Assert.Contains("/t", arguments);
        Assert.Contains("0", arguments);
        Assert.Contains("/c", arguments);
        Assert.DoesNotContain("/m", arguments);
    }

    [Theory]
    [InlineData(HostSystemPowerAction.Sleep, "D2RHost sleep requested. Requested by alice (123).")]
    [InlineData(HostSystemPowerAction.Shutdown, "D2RHost shutdown requested. Requested by alice (123).")]
    [InlineData(HostSystemPowerAction.Restart, "D2RHost restart requested. Requested by alice (123).")]
    public void FormatsDiscordAnnouncementForHostPowerActions(HostSystemPowerAction action, string expected)
    {
        Assert.Equal(expected, HostSystemPowerActions.FormatDiscordAnnouncement(action, "alice (123)"));
    }
}
