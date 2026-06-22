using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class WindowsProcessIdentityTests
{
    [Fact]
    public void D2RTitleFallbackUsesDiabloTitleOnly()
    {
        var needles = WindowsProcessIdentity.GetWindowTitleNeedles(["D2R"]);

        Assert.Contains("Diablo II: Resurrected", needles);
        Assert.DoesNotContain("D2R", needles);
    }

    [Fact]
    public void BattleNetProcessNameKeepsDotNetSuffix()
    {
        var names = WindowsProcessIdentity.GetConfiguredProcessNames(
            "Battle.net",
            ["Battle.net Launcher", @"C:\Program Files (x86)\Battle.net\Battle.net Helper.exe"]);

        Assert.Contains("Battle.net", names);
        Assert.Contains("Battle.net Launcher", names);
        Assert.Contains("Battle.net Helper", names);
        Assert.DoesNotContain("Battle", names);
    }

    [Fact]
    public void D2RProcessNamesAlwaysIncludeRealExecutableName()
    {
        var names = WindowsProcessIdentity.GetD2RProcessNames(
            "D2R_1",
            [@"C:\Games\SomethingElse.exe"]);

        Assert.Contains("D2R", names);
        Assert.Contains("D2R_1", names);
        Assert.Contains("SomethingElse", names);
    }

    [Fact]
    public void CurrentProcessIsRejectedAsAutomationTarget()
    {
        Assert.True(WindowsProcessIdentity.IsCurrentProcess(Environment.ProcessId));
        Assert.False(WindowsProcessIdentity.IsCurrentProcess(Environment.ProcessId + 1));
    }
}
