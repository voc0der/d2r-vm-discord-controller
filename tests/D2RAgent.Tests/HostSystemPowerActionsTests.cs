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

    // /d2r system sleep used to park the master and leave every worker awake, because
    // fleet-wide sleep only happened when someone remembered all:true. Sleep now covers
    // the fleet by default while shutdown/restart stay aimed at one box.
    [Theory]
    [InlineData(HostSystemPowerAction.Sleep, true)]
    [InlineData(HostSystemPowerAction.Shutdown, false)]
    [InlineData(HostSystemPowerAction.Restart, false)]
    public void OnlySleepDefaultsToTheWholeFleet(HostSystemPowerAction action, bool expected)
    {
        Assert.Equal(expected, HostSystemPowerActions.DefaultsToEveryNode(action));
        Assert.Equal(
            expected,
            HostSystemPowerActions.ResolveEveryNode(action, allFlag: null, nodeRequested: false));
    }

    [Theory]
    [InlineData(HostSystemPowerAction.Sleep)]
    [InlineData(HostSystemPowerAction.Shutdown)]
    [InlineData(HostSystemPowerAction.Restart)]
    public void ExplicitScopingOverridesTheSubcommandDefault(HostSystemPowerAction action)
    {
        // all:false is the escape hatch that keeps sleep on the master alone, and naming a
        // node must never be silently widened into a fleet-wide power action.
        Assert.False(HostSystemPowerActions.ResolveEveryNode(action, allFlag: false, nodeRequested: false));
        Assert.False(HostSystemPowerActions.ResolveEveryNode(action, allFlag: null, nodeRequested: true));
        Assert.True(HostSystemPowerActions.ResolveEveryNode(action, allFlag: true, nodeRequested: false));
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
