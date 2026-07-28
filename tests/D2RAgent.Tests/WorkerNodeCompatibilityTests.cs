using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// A worker pinned on v0.2.216 acknowledges system_sleep but lacks v0.2.217's privilege fix. The
// fleet must reject that acknowledgement before it queues sleep on the master, otherwise the
// operator loses the control plane while the worker stays awake.
public sealed class WorkerNodeCompatibilityTests
{
    [Theory]
    [InlineData("0.2.213")]
    [InlineData("0.2.216+old-build")]
    public void WorkerBeforeTheSleepFixIsBlockedWithBootstrapGuidance(string version)
    {
        var reason = WorkerNodeCompatibility.GetSleepBlockReason("server-b", version);

        Assert.NotNull(reason);
        Assert.Contains($"v{version.Split('+')[0]}", reason);
        Assert.Contains("v0.2.217", reason);
        Assert.Contains("v0.2.220", reason);
        Assert.Contains("/d2r system restart node:server-b", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("development")]
    public void UnknownWorkerBuildIsNotTrustedWithFleetSleep(string? version)
    {
        var reason = WorkerNodeCompatibility.GetSleepBlockReason("server-b", version);

        Assert.NotNull(reason);
        Assert.Contains("version is unknown", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v0.2.217", reason);
        Assert.Contains("/d2r system restart node:server-b", reason);
    }

    [Theory]
    [InlineData("0.2.217")]
    [InlineData("0.2.220")]
    [InlineData("0.3.0-local")]
    public void WorkerWithTheSleepFixCanReceiveTheCommand(string version)
    {
        Assert.Null(WorkerNodeCompatibility.GetSleepBlockReason("server-b", version));
    }

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
