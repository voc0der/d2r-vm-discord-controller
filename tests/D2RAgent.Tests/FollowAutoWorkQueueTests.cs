using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoWorkQueueTests
{
    [Theory]
    [InlineData(true)] // A slow menu_ready / join check.
    [InlineData(false)] // A client restart, VM power cycle, or worker restart.
    public async Task HealthyAccountCanAdvanceSeveralGamesWhileAnotherOperationIsHeld(bool gameScoped)
    {
        var held = Completion<string>();
        var queue = new FollowAutoWorkQueue<string>(CancellationToken.None);
        var slow = queue.Enqueue(["broken"], gameScoped, _ => held.Task);

        for (var game = 1; game <= 3; game++)
        {
            var joined = $"healthy joined {game}";
            await queue.Enqueue(["healthy"], true, _ => Task.FromResult(joined));
            Assert.Equal([joined], queue.TakeCompleted());
            Assert.True(queue.IsBusy("BROKEN"));
            Assert.False(queue.IsBusy("healthy"));
            Assert.False(slow.IsCompleted);
            queue.InvalidateChecks();
        }

        held.SetResult("recovered");
        await slow;
        Assert.Equal(gameScoped ? [] : new[] { "recovered" }, queue.TakeCompleted());
        Assert.False(queue.HasWork);
    }

    [Fact]
    public async Task NodeRecoveryReservesItsAccountsAndWaitsOnlyForTheirExistingWork()
    {
        var oldCheck = Completion<string>();
        var otherNode = Completion<string>();
        var recovery = Completion<string>();
        var recoveryStarted = Completion<bool>();
        var queue = new FollowAutoWorkQueue<string>(CancellationToken.None);
        var check = queue.Enqueue(["worker-a-1"], true, _ => oldCheck.Task);
        var other = queue.Enqueue(["worker-b-1"], false, _ => otherNode.Task);
        queue.InvalidateChecks(["WORKER-A-1", "worker-a-2"]);
        var restart = queue.Enqueue(["worker-a-1", "worker-a-2"], false, _ =>
        {
            recoveryStarted.SetResult(true);
            return recovery.Task;
        });

        Assert.True(queue.IsBusy("WORKER-A-2"));
        Assert.False(recoveryStarted.Task.IsCompleted);
        oldCheck.SetResult("obsolete join");
        await check;
        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(queue.TakeCompleted());
        Assert.True(queue.IsBusy("worker-a-1"));
        Assert.False(other.IsCompleted);

        recovery.SetResult("worker-a recovered");
        await restart;
        Assert.Equal(["worker-a recovered"], queue.TakeCompleted());
        Assert.False(queue.IsBusy("worker-a-1"));
        Assert.True(queue.IsBusy("worker-b-1"));
        otherNode.SetResult("worker-b recovered");
        await queue.DrainAsync();
    }

    [Fact]
    public async Task AdvancingGameDiscardsEvenAnAlreadyCompletedButUnconsumedJoin()
    {
        var queue = new FollowAutoWorkQueue<string>(CancellationToken.None);
        await queue.Enqueue(["late"], true, _ => Task.FromResult("old joined"));
        queue.InvalidateChecks();

        Assert.True(queue.IsBusy("late"));
        Assert.Empty(queue.TakeCompleted());
        Assert.False(queue.IsBusy("late"));

        await queue.Enqueue(["late"], true, _ => Task.FromResult("fresh joined"));
        Assert.Equal(["fresh joined"], queue.TakeCompleted());
    }

    [Fact]
    public async Task StopCancelsWarmupAndWaitsForItsCleanupBeforeRunCanUnwind()
    {
        using var cts = new CancellationTokenSource();
        var started = Completion<bool>();
        var cancelled = Completion<bool>();
        var cleanup = Completion<bool>();
        var queue = new FollowAutoWorkQueue<string>(cts.Token);
        var warmup = queue.Enqueue(["vm"], true, async token =>
        {
            started.SetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unexpected";
            }
            finally
            {
                cancelled.SetResult(true);
                await cleanup.Task;
            }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = queue.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.True(queue.IsBusy("vm"));
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = queue.Enqueue(["other"], true, _ => Task.FromResult("no"));
        });

        cleanup.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.True(warmup.IsCanceled);
    }

    [Fact]
    public async Task StopDoesNotStartQueuedRecoveryAfterItsPredecessorFinishes()
    {
        using var cts = new CancellationTokenSource();
        var held = Completion<string>();
        var started = Completion<bool>();
        var queue = new FollowAutoWorkQueue<string>(cts.Token);
        var check = queue.Enqueue(["vm"], true, _ =>
        {
            started.SetResult(true);
            return held.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recoveryCalled = false;
        var recovery = queue.Enqueue(["vm"], false, _ =>
        {
            recoveryCalled = true;
            return Task.FromResult("restarted");
        });

        cts.Cancel();
        held.SetResult("old result");
        await check;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.False(recoveryCalled);
    }

    [Fact]
    public async Task InvalidatedFailuresAreObservedInsteadOfSilentlyDropped()
    {
        var queue = new FollowAutoWorkQueue<string>(CancellationToken.None);
        var failed = queue.Enqueue(["vm"], true, _ => Task.FromException<string>(new InvalidOperationException("failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        queue.InvalidateChecks();

        Assert.Throws<InvalidOperationException>(() => queue.TakeCompleted());
        Assert.False(queue.HasWork);
    }

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
