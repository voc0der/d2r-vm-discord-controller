using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The updater script blocks on Wait-Process for the process that launched it, and only then
// replaces files and restarts the exe. A satellite that reports "update started" and then keeps
// running is therefore never updated: the script waits forever on a process that never exits and
// the exe stays locked. The VM agent has always answered self_update with ExitAfterResult set;
// the worker D2RHost did not, so a node reported "update started" on release after release and
// stayed pinned to the same build while every VM around it moved on.
public sealed class WorkerSelfUpdateExitTests
{
    [Fact]
    public void AStartedUpdateMustAskTheProcessToExit()
    {
        var result = WorkerNodeOperations.BuildSelfUpdateCommandResult(
            BuildSelfUpdateResult(updateStarted: true));

        Assert.True(result.ExitAfterResult);
    }

    [Fact]
    public void AnUpdateThatDidNotStartMustNotKillTheProcess()
    {
        // "Already current" is the common answer on every sweep. Exiting on that would restart
        // the whole fleet every five minutes.
        var result = WorkerNodeOperations.BuildSelfUpdateCommandResult(
            BuildSelfUpdateResult(updateStarted: false));

        Assert.False(result.ExitAfterResult);
    }

    [Fact]
    public void FailedUpdateMustNotKillTheProcess()
    {
        var result = WorkerNodeOperations.BuildSelfUpdateCommandResult(
            BuildSelfUpdateResult(updateStarted: false, ok: false));

        Assert.False(result.Ok);
        Assert.False(result.ExitAfterResult);
    }

    private static AgentCommon.SelfUpdateResult BuildSelfUpdateResult(
        bool updateStarted,
        bool ok = true)
    {
        return new AgentCommon.SelfUpdateResult(
            Ok: ok,
            CheckedLatest: true,
            UpdateAvailable: updateStarted,
            UpdateStarted: updateStarted,
            Message: updateStarted ? "Updater started." : "Already current.",
            CurrentVersion: "0.2.219",
            LatestVersion: "0.2.220",
            LogPath: updateStarted ? @"C:\Temp\update.log" : null);
    }
}
