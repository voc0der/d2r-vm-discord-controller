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
    public void D2RTitleFallbackRejectsBattleNetProductWindow()
    {
        Assert.False(WindowsProcessIdentity.IsWindowTitleMatch(
            ["D2R"],
            "Battle.net",
            "Diablo II: Resurrected - Battle.net"));
    }

    [Fact]
    public void D2RTitleFallbackAcceptsD2RWindow()
    {
        Assert.True(WindowsProcessIdentity.IsWindowTitleMatch(
            ["D2R"],
            "D2R",
            "Diablo II: Resurrected"));
    }

    [Fact]
    public void D2RTitleFallbackAcceptsExactTitleWhenProcessNameIsUnavailable()
    {
        Assert.True(WindowsProcessIdentity.IsWindowTitleMatch(
            ["D2R"],
            "",
            "Diablo II: Resurrected"));
    }

    [Fact]
    public void D2RTitleFallbackRejectsDecoratedTitleWhenProcessNameIsUnavailable()
    {
        Assert.False(WindowsProcessIdentity.IsWindowTitleMatch(
            ["D2R"],
            "",
            "Diablo II: Resurrected - Battle.net"));
    }

    [Fact]
    public void CurrentProcessIsRejectedAsAutomationTarget()
    {
        Assert.True(WindowsProcessIdentity.IsCurrentProcess(Environment.ProcessId));
        Assert.False(WindowsProcessIdentity.IsCurrentProcess(Environment.ProcessId + 1));
    }

    [Fact]
    public void D2RFallbackNeedlesExcludeBattleNetTerms()
    {
        var needles = WindowsProcessIdentity.GetFallbackProcessNameNeedles(["D2R"]);

        Assert.Contains("d2r", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("diablo", needles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("battle.net", needles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("blizzard", needles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BattleNetFallbackNeedlesExcludeD2RTerms()
    {
        var needles = WindowsProcessIdentity.GetFallbackProcessNameNeedles(["Battle.net"]);

        Assert.Contains("battle.net", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("battlenet", needles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("blizzard", needles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("d2r", needles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("diablo", needles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownProcessNameProducesNoFallbackNeedles()
    {
        var needles = WindowsProcessIdentity.GetFallbackProcessNameNeedles(["SomeUnrelatedTool"]);

        Assert.Empty(needles);
    }
}
