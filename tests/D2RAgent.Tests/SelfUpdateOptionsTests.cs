using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class SelfUpdateOptionsTests
{
    [Fact]
    public void HostSelfUpdateCanRestartScheduledTask()
    {
        var options = SelfUpdateOptions.D2RHost([]);

        Assert.Equal("D2R Host Controller", options.RestartScheduledTaskName);
    }

    [Fact]
    public void AgentSelfUpdateCanRestartScheduledTask()
    {
        var options = SelfUpdateOptions.D2RAgent([]);

        Assert.Equal("D2R VM Agent", options.RestartScheduledTaskName);
    }
}
