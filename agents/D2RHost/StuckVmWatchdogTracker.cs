namespace D2RHost;

/// <summary>
/// Per-account bookkeeping for the stuck-VM sweep: how long each agent has been continuously
/// missing, how much of its recovery budget has been spent, and whether the operator has already
/// been told that budget ran out.
/// </summary>
/// <remarks>
/// The offline clock is measured from this host's own observations rather than from the agent's
/// persisted last-seen timestamp. A last-seen value can be hours old and survive a host restart,
/// which would make the very first sweep after startup look like a fleet-wide hang and power-cycle
/// every VM while they were still booting. Counting only uninterrupted offline observations made by
/// the running process cannot produce that, and costs nothing but patience after a restart.
/// </remarks>
internal sealed class StuckVmWatchdogTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AccountState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one observation of a missing agent and returns how long its current offline streak
    /// has lasted. The first observation of a streak returns <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan RecordOffline(string accountKey, DateTimeOffset now)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountState();
                _states[accountKey] = state;
            }

            state.OnlineSince = null;
            state.OfflineSince ??= now;
            var offlineFor = now - state.OfflineSince.Value;
            return offlineFor < TimeSpan.Zero ? TimeSpan.Zero : offlineFor;
        }
    }

    /// <summary>
    /// Records that an agent is connected, refunding its recovery budget once it has stayed that
    /// way for <paramref name="stableFor"/>.
    /// </summary>
    /// <remarks>
    /// The streak clears immediately - the guest is plainly not silent right now - but the budget
    /// deliberately does not. Refunding on the first sighting makes <c>maxRecoveriesPerVm</c>
    /// unenforceable against the failure it most needs to bound: a guest whose agent starts,
    /// connects, and dies again. Each brief appearance would hand back the whole allowance, so the
    /// watchdog would power-cycle that guest every few minutes forever and the dead-end notice -
    /// the one thing that tells an operator to go look at it - could never fire.
    ///
    /// Staying connected for as long as the silence that would have condemned it is what proves the
    /// recovery actually worked, so that is the bar for a refund. A guest that clears it and wedges
    /// again weeks later starts from a full budget, which was the original intent.
    /// </remarks>
    public void RecordOnline(string accountKey, DateTimeOffset now, TimeSpan stableFor)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                return;
            }

            state.OfflineSince = null;
            state.OnlineSince ??= now;
            if (now - state.OnlineSince.Value >= stableFor)
            {
                _states.Remove(accountKey);
            }
        }
    }

    /// <summary>
    /// Drops the offline streak without touching the recovery budget, for a sweep that could not
    /// observe the guest at all.
    /// </summary>
    /// <remarks>
    /// Silence the host could not see is not evidence about the guest. Every VM agent on a worker
    /// node reaches the master through that worker's own process, so a worker that restarts - a
    /// self-update is enough - takes all of its agents offline with it while every one of its
    /// guests keeps running perfectly. Counting that would power-cycle a whole node's healthy VMs
    /// the moment the worker came back, before its agents had finished re-registering.
    /// </remarks>
    public void RecordUnobserved(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (_states.TryGetValue(accountKey, out var state))
            {
                state.OfflineSince = null;
            }
        }
    }

    /// <summary>
    /// Restarts the offline clock without charging the budget, for a caller that has just finished
    /// acting on this guest. A recovery runs for as long as half an hour, so the clock restarted
    /// when the attempt was charged is already far past the grace window by the time it returns.
    /// </summary>
    public void RestartClock(string accountKey, DateTimeOffset now)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (_states.TryGetValue(accountKey, out var state))
            {
                state.OfflineSince = now;
            }
        }
    }

    /// <summary>
    /// Records that this guest's console is showing the Windows boot logo right now, and returns
    /// how long it has been showing it without interruption.
    /// </summary>
    /// <remarks>
    /// Continuity is the whole signal. A single sighting says nothing - every healthy VM shows this
    /// logo on every boot - so what distinguishes a wedged guest is only that it never leaves. Any
    /// frame that is not the logo clears the clock, so a guest that boots through normally can
    /// never accumulate a streak no matter how often it is sampled mid-boot.
    /// </remarks>
    public TimeSpan RecordBootLogo(string accountKey, DateTimeOffset now)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountState();
                _states[accountKey] = state;
            }

            state.BootLogoSince ??= now;
            var heldFor = now - state.BootLogoSince.Value;
            return heldFor < TimeSpan.Zero ? TimeSpan.Zero : heldFor;
        }
    }

    /// <summary>
    /// Clears the boot-logo clock, for any observation that is not the logo - including one that
    /// could not be taken at all, since a frame the host failed to capture is not evidence the
    /// guest is still stuck.
    /// </summary>
    public void ClearBootLogo(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (_states.TryGetValue(accountKey, out var state))
            {
                state.BootLogoSince = null;
            }
        }
    }

    public int BootLogoCutsUsed(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _states.TryGetValue(accountKey, out var state) ? state.BootLogoCutsUsed : 0;
        }
    }

    /// <summary>
    /// Charges one boot-logo power cut and restarts the logo clock, so the guest gets a full fresh
    /// window to boot before the watchdog considers cutting it again.
    /// </summary>
    public void RecordBootLogoCut(string accountKey, DateTimeOffset now)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountState();
                _states[accountKey] = state;
            }

            state.BootLogoCutsUsed++;
            state.BootLogoSince = null;
            state.OfflineSince = now;
        }
    }

    public int RecoveriesUsed(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _states.TryGetValue(accountKey, out var state) ? state.RecoveriesUsed : 0;
        }
    }

    /// <summary>
    /// Charges one power cycle against the guest's budget and restarts its offline clock, so the
    /// next attempt needs another full grace window of silence rather than firing again on the
    /// next sweep while the VM is still booting.
    /// </summary>
    public void RecordRecoveryAttempt(string accountKey, DateTimeOffset now)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountState();
                _states[accountKey] = state;
            }

            state.RecoveriesUsed++;
            state.OfflineSince = now;
        }
    }

    /// <summary>
    /// Claims the one-shot right to announce that a guest is out of recovery attempts, so a sweep
    /// running every minute reports the dead end once instead of forever.
    /// </summary>
    public bool TryClaimGiveUpNotice(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state) || state.GiveUpNotified)
            {
                return false;
            }

            state.GiveUpNotified = true;
            return true;
        }
    }

    /// <summary>
    /// Drops every streak. Called when the physical host is detected to have resumed: the wall
    /// clock advanced by however long the machine was suspended, and none of that elapsed time is
    /// evidence about a guest that was suspended right along with it. Without this the first sweep
    /// after a resume would read every VM as hours-stuck and cycle the whole fleet.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _states.Clear();
        }
    }

    private static string RequireKey(string accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new ArgumentException("Account key is required.", nameof(accountKey));
        }

        return accountKey.Trim();
    }

    private sealed class AccountState
    {
        public DateTimeOffset? OfflineSince { get; set; }

        /// <summary>
        /// When the current uninterrupted run of connected observations began, or null while the
        /// agent is missing. Only a run long enough to prove a recovery worked refunds the budget.
        /// </summary>
        public DateTimeOffset? OnlineSince { get; set; }

        public int RecoveriesUsed { get; set; }

        public bool GiveUpNotified { get; set; }

        /// <summary>
        /// When this guest's console was first seen showing the Windows boot logo without a break,
        /// or null whenever the last frame was anything else.
        /// </summary>
        public DateTimeOffset? BootLogoSince { get; set; }

        public int BootLogoCutsUsed { get; set; }
    }
}
