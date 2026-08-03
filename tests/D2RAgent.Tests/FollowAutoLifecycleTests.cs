using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoLifecycleTests
{
    [Fact]
    public async Task SuccessorWaitsForExactUnwindAndStopJournalCleanup()
    {
        var lifecycle = new FollowAutoLifecycle(initialSequence: 100);
        var oldRun = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var stopEntered = NewSignal();
        using var releaseStop = new ManualResetEventSlim();

        var cancelTask = Task.Run(() => lifecycle.CancelAsync(
            "operator stop",
            _ => { },
            () =>
            {
                stopEntered.TrySetResult();
                releaseStop.Wait();
            }));

        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldRun.CancellationRequested);

        var successorTask = lifecycle.TryBeginAsync();
        await Task.Yield();
        Assert.False(successorTask.IsCompleted);

        // Even a fully returned old loop cannot expose the slot while Stop's serialized durable
        // cleanup is still pending.
        await lifecycle.CompleteUnwindAsync(oldRun);
        await Task.Yield();
        Assert.False(successorTask.IsCompleted);

        releaseStop.Set();
        var canceled = await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        var successor = Assert.IsType<FollowAutoRunLease>(
            await successorTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(canceled.WasRunning);
        Assert.Equal(oldRun.RunId, canceled.RunId);
        Assert.NotSame(oldRun, successor);
        Assert.True(successor.RunId > oldRun.RunId);
    }

    [Fact]
    public async Task StopPublishesCancellationWhileRecoveryOwnsJournalGate()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var recoveryEntered = NewSignal();
        var continueRecovery = NewSignal();
        var intentRecorded = false;
        var restartQueued = false;

        var recoveryTask = lifecycle.WithCurrentRunAsync(
            run,
            async token =>
            {
                intentRecorded = true;
                recoveryEntered.TrySetResult();
                await continueRecovery.Task;
                token.ThrowIfCancellationRequested();
                restartQueued = true;
                return true;
            });

        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelTask = lifecycle.CancelAsync(
            "operator stop",
            _ => { },
            () => intentRecorded = false);

        // This is the deadlock-sensitive interleaving: Stop must publish through the state gate
        // even though recovery still owns the independent journal gate.
        Assert.True(run.CancellationRequested);
        Assert.False(cancelTask.IsCompleted);

        continueRecovery.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recoveryTask);
        var canceled = await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(canceled.WasRunning);
        Assert.False(intentRecorded);
        Assert.False(restartQueued);
    }

    [Fact]
    public async Task StopWinningBeforeRecoveryPreventsJournalRewrite()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var intentRecorded = true;
        var armedAfterStop = false;

        await lifecycle.CancelAsync(
            "operator stop",
            _ => { },
            () => intentRecorded = false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lifecycle.WithCurrentRunAsync(
                run,
                _ =>
                {
                    armedAfterStop = true;
                    intentRecorded = true;
                    return Task.FromResult(true);
                }));

        Assert.False(armedAfterStop);
        Assert.False(intentRecorded);
    }

    [Fact]
    public async Task RecoveryQueuedFirstIsFollowedByStopCleanup()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var queueEntered = NewSignal();
        var finishQueue = NewSignal();
        var intentRecorded = false;
        var restartQueued = false;

        var recoveryTask = lifecycle.WithCurrentRunAsync(
            run,
            async token =>
            {
                intentRecorded = true;
                token.ThrowIfCancellationRequested();
                queueEntered.TrySetResult();
                await finishQueue.Task;
                restartQueued = true;
                return true;
            });

        await queueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelTask = lifecycle.CancelAsync(
            "operator stop",
            _ => { },
            () => intentRecorded = false);
        Assert.True(run.CancellationRequested);

        finishQueue.TrySetResult();
        Assert.True(await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(restartQueued);
        Assert.False(intentRecorded);
    }

    [Fact]
    public async Task RepeatStopDoesNotOverwriteFirstCancellationMetadata()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var beforeCancellationCalls = 0;

        var first = await lifecycle.CancelAsync(
            "first reason",
            _ => beforeCancellationCalls++,
            () => { });
        var repeat = await lifecycle.CancelAsync(
            "second reason",
            _ => beforeCancellationCalls++,
            () => { });

        Assert.True(first.WasRunning);
        Assert.Equal(run.RunId, first.RunId);
        Assert.False(repeat.WasRunning);
        Assert.Equal(0, repeat.RunId);
        Assert.Equal("first reason", run.StopReason);
        Assert.Equal(1, beforeCancellationCalls);
    }

    [Fact]
    public async Task BeginPreconditionIsRecheckedAfterCanceledRunUnwinds()
    {
        var lifecycle = new FollowAutoLifecycle();
        var oldRun = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        await lifecycle.CancelAsync("stop", _ => { }, () => { });
        var mayResume = true;

        var resumeTask = lifecycle.TryBeginAsync(() => mayResume);
        await Task.Yield();
        Assert.False(resumeTask.IsCompleted);

        mayResume = false;
        await lifecycle.CompleteUnwindAsync(oldRun);

        Assert.Null(await resumeTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExactFallbackWaitsForItsMatchingPredecessorToUnwind()
    {
        var lifecycle = new FollowAutoLifecycle();
        var predecessor = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());

        var fallback = lifecycle.TryBeginAsync(waitForActiveRunId: predecessor.RunId);
        await Task.Yield();
        Assert.False(fallback.IsCompleted);

        await lifecycle.CompleteUnwindAsync(predecessor);
        var resumed = Assert.IsType<FollowAutoRunLease>(
            await fallback.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotEqual(predecessor.RunId, resumed.RunId);
        await lifecycle.CompleteUnwindAsync(resumed);
    }

    [Fact]
    public async Task ExactFallbackDoesNotWaitForAnUnrelatedActiveRun()
    {
        var lifecycle = new FollowAutoLifecycle();
        var active = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());

        Assert.Null(await lifecycle.TryBeginAsync(waitForActiveRunId: active.RunId + 100));

        await lifecycle.CompleteUnwindAsync(active);
    }

    [Fact]
    public async Task StaleOrDoubleCompletionCannotClearNewRun()
    {
        var lifecycle = new FollowAutoLifecycle();
        var first = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        await lifecycle.CompleteUnwindAsync(first);
        var second = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());

        await lifecycle.CompleteUnwindAsync(first);

        Assert.True(lifecycle.IsRunning);
        Assert.Null(await lifecycle.TryBeginAsync());

        await lifecycle.CompleteUnwindAsync(second);
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillCleansJournalBarrierAndReleasesLease()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        var journalCleaned = false;
        using var registration = run.Token.Register(
            () => throw new InvalidOperationException("broken cancellation callback"));

        await Assert.ThrowsAnyAsync<Exception>(() => lifecycle.CancelAsync(
            "stop",
            _ => { },
            () => journalCleaned = true));

        Assert.True(journalCleaned);
        await lifecycle.CompleteUnwindAsync(run);
        Assert.IsType<FollowAutoRunLease>(
            await lifecycle.TryBeginAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ActiveRunMutationIsAtomicWithStopAndCannotReachASuccessor()
    {
        var lifecycle = new FollowAutoLifecycle();
        var first = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        using var mutationEntered = new ManualResetEventSlim();
        using var releaseMutation = new ManualResetEventSlim();
        var mutatedRunId = 0L;

        var mutationTask = Task.Run(() => lifecycle.WithActiveRunAsync(
            run =>
            {
                mutationEntered.Set();
                releaseMutation.Wait();
                mutatedRunId = run.RunId;
                return true;
            },
            inactiveResult: false));

        Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(5)));
        var cancelTask = lifecycle.CancelAsync("stop", _ => { }, () => { });
        Assert.False(first.CancellationRequested);

        releaseMutation.Set();
        Assert.True(await mutationTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        await lifecycle.CompleteUnwindAsync(first);
        var successor = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());

        Assert.Equal(first.RunId, mutatedRunId);
        Assert.NotEqual(successor.RunId, mutatedRunId);
    }

    [Fact]
    public async Task CanceledRunRefusesDelayedControlMutation()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        await lifecycle.CancelAsync("stop", _ => { }, () => { });
        var actionRan = false;

        var result = await lifecycle.WithActiveRunAsync(
            _ =>
            {
                actionRan = true;
                return "mutated";
            },
            inactiveResult: "inactive");

        Assert.Equal("inactive", result);
        Assert.False(actionRan);
        await lifecycle.CompleteUnwindAsync(run);
    }

    [Fact]
    public async Task StaleMonitorStopCannotCancelOrClearASuccessor()
    {
        var lifecycle = new FollowAutoLifecycle();
        var first = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        first.AssociateMonitorMessage(100);
        var capturedFirst = Assert.IsType<FollowAutoRunLease>(
            await lifecycle.TryCaptureMonitorRunAsync(100));
        await lifecycle.CancelAsync("first stop", _ => { }, () => { });
        await lifecycle.CompleteUnwindAsync(first);
        var successor = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        successor.AssociateMonitorMessage(200);
        var serializedStopStateRan = false;

        var staleStop = await lifecycle.CancelAsync(
            "stale monitor stop",
            _ => { },
            () => serializedStopStateRan = true,
            expectedRun: capturedFirst);

        Assert.True(staleStop.Rejected);
        Assert.False(staleStop.WasRunning);
        Assert.False(serializedStopStateRan);
        Assert.False(successor.CancellationRequested);
        Assert.True(lifecycle.IsRunning);
        await lifecycle.CompleteUnwindAsync(successor);
    }

    [Fact]
    public async Task CapturedMonitorStopStillCleansUpAfterItsRunUnwindsDuringDefer()
    {
        var lifecycle = new FollowAutoLifecycle();
        var run = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        run.AssociateMonitorMessage(100);
        var captured = Assert.IsType<FollowAutoRunLease>(
            await lifecycle.TryCaptureMonitorRunAsync(100));
        Assert.Null(await lifecycle.TryCaptureMonitorRunAsync(999));

        // Models Discord DeferAsync yielding while the terminal monitor path finishes the run.
        await lifecycle.CompleteUnwindAsync(run);
        var serializedStopStateRan = false;
        var stop = await lifecycle.CancelAsync(
            "captured stop",
            _ => { },
            () => serializedStopStateRan = true,
            expectedRun: captured);

        Assert.False(stop.Rejected);
        Assert.False(stop.WasRunning);
        Assert.True(serializedStopStateRan);
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public async Task CapturedMonitorStopCannotCleanUpAfterASuccessorAlreadyUnwound()
    {
        var lifecycle = new FollowAutoLifecycle();
        var first = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        first.AssociateMonitorMessage(100);
        var capturedFirst = Assert.IsType<FollowAutoRunLease>(
            await lifecycle.TryCaptureMonitorRunAsync(100));
        await lifecycle.CompleteUnwindAsync(first);

        var successor = Assert.IsType<FollowAutoRunLease>(await lifecycle.TryBeginAsync());
        successor.AssociateMonitorMessage(200);
        await lifecycle.CompleteUnwindAsync(successor);
        var serializedStopStateRan = false;

        var staleStop = await lifecycle.CancelAsync(
            "stale monitor stop",
            _ => { },
            () => serializedStopStateRan = true,
            expectedRun: capturedFirst);

        Assert.True(staleStop.Rejected);
        Assert.False(serializedStopStateRan);
        Assert.False(lifecycle.IsRunning);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
