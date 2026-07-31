using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Version checks remain only for explaining the historical self-update bootstrap. Host power
// commands are guarded by an explicit current-connection capability instead.
public sealed class WorkerNodeCompatibilityTests
{
    [Theory]
    [InlineData("0.2.213", true)]
    [InlineData("0.2.219", true)]
    [InlineData("0.2.220", false)]
    [InlineData("0.3.0", false)]
    public void OnlyPre220WorkersNeedOneProcessRestartToFinishSelfUpdate(
        string version,
        bool expected)
    {
        Assert.Equal(expected, WorkerNodeCompatibility.NeedsSelfUpdateBootstrap(version));
    }
}
