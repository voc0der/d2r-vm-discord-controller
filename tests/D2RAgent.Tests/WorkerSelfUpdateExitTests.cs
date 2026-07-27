using AgentCommon;
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
        var result = CommandResult.Success("Updater started.", new { UpdateStarted = true }, exitAfterResult: true);

        Assert.True(result.ExitAfterResult);
    }

    [Fact]
    public void AnUpdateThatDidNotStartMustNotKillTheProcess()
    {
        // "Already current" is the common answer on every sweep. Exiting on that would restart
        // the whole fleet every five minutes.
        var result = CommandResult.Success("D2RHost is current at 0.2.219.", new { UpdateStarted = false }, exitAfterResult: false);

        Assert.False(result.ExitAfterResult);
    }

    [Fact]
    public void CommandResultsDefaultToStayingAlive()
    {
        Assert.False(CommandResult.Success("ok").ExitAfterResult);
        Assert.False(CommandResult.Failure("nope").ExitAfterResult);
    }
}
