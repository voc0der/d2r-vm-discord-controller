using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class SelfUpdaterTests
{
    [Fact]
    public void PublishedWindowsExePathAllowsRenamedExecutable()
    {
        var ok = SelfUpdater.IsPublishedWindowsExePath(
            @"C:\D2ROps\OpsHost.exe",
            _ => true,
            out var message);

        Assert.True(ok);
        Assert.Equal("", message);
    }

    [Fact]
    public void PublishedWindowsExePathRejectsDotnetOrDllLaunch()
    {
        var ok = SelfUpdater.IsPublishedWindowsExePath(
            @"C:\D2ROps\D2RHost.dll",
            _ => true,
            out var message);

        Assert.False(ok);
        Assert.Equal("Current process is not a published Windows exe.", message);
    }
}
