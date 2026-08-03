namespace D2RHost;

/// <summary>
/// Owns the exact lifetime of a follow-auto loop. A canceled lease remains current until its loop
/// has returned completely, so no successor can share or be damaged by the predecessor's global
/// monitor cleanup. The same gate serializes explicit Stop against local restart journaling.
/// </summary>
internal sealed class FollowAutoLifecycle
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _localRestartGate = new(1, 1);
    private FollowAutoRunLease? _current;
    private long _sequence;
    private int _stopCleanupCount;
    private TaskCompletionSource? _stopCleanupFinished;

    public FollowAutoLifecycle(long initialSequence = 0)
    {
        _sequence = initialSequence;
    }

    public bool IsRunning => Volatile.Read(ref _current) is not null;

    /// <summary>
    /// Performs one short synchronous read/mutation against the active run while lifecycle
    /// transitions are excluded. This is for gateway controls whose validation and mutation must
    /// be one operation: otherwise an old control can validate, yield, and mutate a successor.
    /// </summary>
    public async Task<T> WithActiveRunAsync<T>(
        Func<FollowAutoRunLease, T> action,
        T inactiveResult)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _stateGate.WaitAsync();
        try
        {
            return _current is { CancellationRequested: false } current
                ? action(current)
                : inactiveResult;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Captures the exact active lease that rendered a monitor control. Callers do this before a
    /// network acknowledgement can yield; cancellation later uses the lease identity rather than
    /// a mutable process-global monitor reference, so it can distinguish an unwound predecessor
    /// from a successor even if the Discord defer completed after the transition.
    /// </summary>
    public async Task<FollowAutoRunLease?> TryCaptureMonitorRunAsync(ulong monitorMessageId)
    {
        await _stateGate.WaitAsync();
        try
        {
            return _current is { CancellationRequested: false } current
                && current.MonitorMessageId == monitorMessageId
                    ? current
                    : null;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<FollowAutoRunLease?> TryBeginAsync(
        Func<bool>? canBegin = null,
        Action? initialize = null,
        long? waitForActiveRunId = null)
    {
        while (true)
        {
            Task? waitFor = null;
            await _stateGate.WaitAsync();
            try
            {
                if (_stopCleanupCount > 0)
                {
                    waitFor = _stopCleanupFinished!.Task;
                }
                else if (_current is null)
                {
                    if (canBegin is not null && !canBegin())
                    {
                        return null;
                    }

                    var lease = new FollowAutoRunLease(++_sequence);
                    // Initialization is part of lease installation. In particular, resetting any
                    // predecessor-owned global presentation state here cannot race a Stop for the
                    // new lease because both operations use this state gate.
                    initialize?.Invoke();
                    Volatile.Write(ref _current, lease);
                    return lease;
                }

                else if (_current.CancellationRequested
                    || _current.RunId == waitForActiveRunId)
                {
                    waitFor = _current.Unwound;
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            // Wait outside the gate: Stop and the old loop both need it to publish completion.
            await waitFor!;
        }
    }

    public async Task<FollowAutoLifecycleCancel> CancelAsync(
        string? reason,
        Action<bool> beforeCancellation,
        Action serializedStopState,
        FollowAutoRunLease? expectedRun = null)
    {
        ArgumentNullException.ThrowIfNull(beforeCancellation);
        ArgumentNullException.ThrowIfNull(serializedStopState);
        FollowAutoRunLease? current = null;
        var wasRunning = false;
        var rejected = false;
        var cleanupStarted = false;
        Exception? cancellationFailure = null;

        try
        {
            await _stateGate.WaitAsync();
            try
            {
                current = _current;
                if (expectedRun is not null
                    && (current is not null
                        ? !ReferenceEquals(current, expectedRun)
                        : _sequence != expectedRun.RunId))
                {
                    // A captured monitor control may outlive the run that rendered it. Null means
                    // that exact predecessor already unwound and is still safe to clean up only
                    // when no successor was installed in between. A different current lease or a
                    // later sequence is a successor and must be left entirely untouched.
                    rejected = true;
                }
                else
                {
                    BeginStopCleanup();
                    cleanupStarted = true;
                }

                wasRunning = !rejected && current is { CancellationRequested: false };

                // Publish every per-run Stop side effect before the token. The canceled loop may
                // reach completion immediately after CancellationTokenSource.Cancel returns. A
                // repeat Stop does not overwrite the first Stop's reason/action metadata.
                if (wasRunning)
                {
                    try
                    {
                        beforeCancellation(true);
                    }
                    catch (Exception ex)
                    {
                        cancellationFailure = ex;
                    }

                    try
                    {
                        current!.Cancel(reason);
                    }
                    catch (Exception ex)
                    {
                        cancellationFailure = cancellationFailure is null
                            ? ex
                            : new AggregateException(cancellationFailure, ex);
                    }
                }
            }
            finally
            {
                _stateGate.Release();
            }

            if (rejected)
            {
                return new FollowAutoLifecycleCancel(
                    WasRunning: false,
                    RunId: 0,
                    Rejected: true);
            }

            // Cancellation is already observable. Wait without the state gate so recovery can
            // finish (or abort) its journal transaction and release this gate.
            await _localRestartGate.WaitAsync();
            try
            {
                // This must run even when no loop is active: an explicit Stop also cancels a
                // durable one-shot resume left by a recovery attempt or previous process.
                serializedStopState();
            }
            finally
            {
                _localRestartGate.Release();
            }

            if (cancellationFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(cancellationFailure)
                    .Throw();
            }

            return new FollowAutoLifecycleCancel(
                wasRunning,
                wasRunning ? current!.RunId : 0,
                Rejected: false);
        }
        finally
        {
            if (cleanupStarted)
            {
                await FinishStopCleanupAsync();
            }
        }
    }

    /// <summary>
    /// Runs local restart Arm + journal Save + cancellation recheck + queue under the journal gate
    /// used by Stop. Stop can publish cancellation independently while this action is active, then
    /// waits to clear the intent before a successor run can install.
    /// </summary>
    public async Task<T> WithCurrentRunAsync<T>(
        FollowAutoRunLease expected,
        Func<CancellationToken, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(action);
        // Take the journal gate without the state gate. Stop publishes cancellation under the
        // state gate and never waits on this gate until after releasing state, so this ordering
        // cannot deadlock.
        await _localRestartGate.WaitAsync();
        try
        {
            // Stop may have won while this call waited for the journal gate.
            await _stateGate.WaitAsync();
            try
            {
                ThrowIfNotCurrent(expected);
            }
            finally
            {
                _stateGate.Release();
            }

            expected.Token.ThrowIfCancellationRequested();
            return await action(expected.Token);
        }
        finally
        {
            _localRestartGate.Release();
        }
    }

    public async Task CompleteUnwindAsync(FollowAutoRunLease completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        await _stateGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_current, completed))
            {
                Volatile.Write(ref _current, null);
            }
        }
        finally
        {
            _stateGate.Release();
        }

        completed.CompleteUnwind();
    }

    private void ThrowIfNotCurrent(FollowAutoRunLease expected)
    {
        if (!ReferenceEquals(_current, expected) || expected.CancellationRequested)
        {
            throw new OperationCanceledException(expected.Token);
        }
    }

    private void BeginStopCleanup()
    {
        if (_stopCleanupCount++ == 0)
        {
            _stopCleanupFinished = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task FinishStopCleanupAsync()
    {
        TaskCompletionSource? completed = null;
        await _stateGate.WaitAsync();
        try
        {
            if (--_stopCleanupCount == 0)
            {
                completed = _stopCleanupFinished;
                _stopCleanupFinished = null;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        completed?.TrySetResult();
    }
}

internal sealed class FollowAutoRunLease
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _unwound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _monitorMessageId;

    public FollowAutoRunLease(long runId)
    {
        RunId = runId;
    }

    public long RunId { get; }
    public CancellationToken Token => _cts.Token;
    public bool CancellationRequested => _cts.IsCancellationRequested;
    public string? StopReason { get; private set; }
    public Task Unwound => _unwound.Task;
    public ulong? MonitorMessageId
    {
        get
        {
            var messageId = Volatile.Read(ref _monitorMessageId);
            return messageId == 0 ? null : unchecked((ulong)messageId);
        }
    }

    internal void AssociateMonitorMessage(ulong monitorMessageId)
    {
        if (monitorMessageId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorMessageId));
        }

        var encoded = unchecked((long)monitorMessageId);
        var previous = Interlocked.CompareExchange(ref _monitorMessageId, encoded, 0);
        if (previous != 0 && previous != encoded)
        {
            throw new InvalidOperationException("A follow-auto run cannot own more than one monitor message.");
        }
    }

    internal void Cancel(string? reason)
    {
        StopReason = reason;
        _cts.Cancel();
    }

    internal void CompleteUnwind()
    {
        _cts.Dispose();
        _unwound.TrySetResult();
    }
}

internal readonly record struct FollowAutoLifecycleCancel(bool WasRunning, long RunId, bool Rejected);
