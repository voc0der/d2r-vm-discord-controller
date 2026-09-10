using AgentCommon;

namespace D2RHost;

internal enum DcloneSlotState
{
    /// <summary>Warming the client and creating its game for the first time.</summary>
    Preparing,

    /// <summary>Sitting in its own game, waiting to be joined.</summary>
    Parked,

    /// <summary>Confirmed out of its game; building a replacement game under a new name.</summary>
    Reparking,

    /// <summary>Out of attempts. Left alone until the operator intervenes.</summary>
    Failed,

    /// <summary>Its VM agent went away. Picked back up automatically when it reconnects.</summary>
    Offline
}

internal sealed record DcloneParkSlotSnapshot(
    string AccountKey,
    string DisplayName,
    string AgentId,
    string? GameName,
    string? Password,
    DcloneSlotState State,
    string Detail,
    int Reparks);

/// <summary>
/// The mutable state of one <c>/d2r dclone</c> run: which account holds which game, how each park
/// is doing, and the pool of names the run has already spent.
/// </summary>
/// <remarks>
/// Re-parks run concurrently across accounts, so every read and write goes through one lock and
/// callers only ever see immutable snapshots. Names are never recycled inside a run - a game that
/// looked dead can still be alive on the realm, and re-minting its name would just collide.
/// </remarks>
internal sealed class DcloneParkRun
{
    private readonly object _sync = new();
    private readonly HashSet<string> _spentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Slot> _slots = new();
    private string? _stopReason;

    public DcloneParkRun(long runId, string difficulty, DateTimeOffset startedUtc)
    {
        RunId = runId;
        Difficulty = difficulty;
        StartedUtc = startedUtc;
    }

    public long RunId { get; }
    public string Difficulty { get; }
    public DateTimeOffset StartedUtc { get; }

    /// <summary>
    /// Why the park ended, when something other than the operator's own Stop ended it (a quit-all
    /// or a leave-all). Only the first reason is kept: it is the one that actually stopped the run.
    /// </summary>
    public string? StopReason
    {
        get
        {
            lock (_sync)
            {
                return _stopReason;
            }
        }
    }

    public void RecordStopReason(string? reason)
    {
        lock (_sync)
        {
            _stopReason ??= reason;
        }
    }

    public void AddSlot(string accountKey, string displayName, string agentId)
    {
        lock (_sync)
        {
            _slots.Add(new Slot(accountKey, displayName, agentId));
        }
    }

    /// <summary>
    /// A name/password pair no other slot in this run has held. The bounded retry exists only to
    /// stop a pathological RNG from spinning; at seven characters a repeat is already unlikely
    /// enough that the loop practically never runs twice.
    /// </summary>
    public (string Name, string Password) MintCredentials()
    {
        lock (_sync)
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var name = RandomGameCredentials.NewDcloneGameName();
                if (_spentNames.Add(name))
                {
                    return (name, RandomGameCredentials.NewDclonePassword());
                }
            }

            // Every name this run has ever handed out is in the set, so falling through means the
            // generator is degenerate rather than unlucky. A suffixed name is still joinable and
            // still unique, which is all the caller needs.
            var fallback = RandomGameCredentials.NewDcloneGameName() + _spentNames.Count.ToString("x");
            _spentNames.Add(fallback);
            return (fallback, RandomGameCredentials.NewDclonePassword());
        }
    }

    public void MarkPreparing(string accountKey, string detail)
    {
        Mutate(accountKey, slot =>
        {
            slot.State = DcloneSlotState.Preparing;
            slot.Detail = detail;
        });
    }

    public void MarkParked(string accountKey, string gameName, string password, string detail)
    {
        Mutate(accountKey, slot =>
        {
            slot.State = DcloneSlotState.Parked;
            slot.GameName = gameName;
            slot.Password = password;
            slot.Detail = detail;
            slot.OutOfGameStreak = 0;
            slot.ReparkInFlight = false;
            slot.FailedAtUtc = null;
        });
    }

    public void MarkFailed(string accountKey, string detail, DateTimeOffset nowUtc)
    {
        Mutate(accountKey, slot =>
        {
            slot.State = DcloneSlotState.Failed;
            slot.GameName = null;
            slot.Password = null;
            slot.Detail = detail;
            slot.OutOfGameStreak = 0;
            slot.ReparkInFlight = false;
            slot.FailedAtUtc = nowUtc;
        });
    }

    public void MarkOffline(string accountKey, string detail)
    {
        Mutate(accountKey, slot =>
        {
            slot.State = DcloneSlotState.Offline;
            slot.Detail = detail;
            slot.OutOfGameStreak = 0;
        });
    }

    /// <summary>
    /// Folds one status reading into the slot and answers whether this reading is the one that
    /// tips it into a rebuild. Streak accounting and the in-flight latch are set together so two
    /// consecutive polls can never start two re-parks for the same account.
    /// </summary>
    public bool RegisterReadingAndTryBeginRepark(
        string accountKey,
        DcloneParkPresence presence,
        DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var slot = Find(accountKey);
            if (slot is null || slot.ReparkInFlight)
            {
                return false;
            }

            if (presence == DcloneParkPresence.Offline)
            {
                slot.State = DcloneSlotState.Offline;
                slot.Detail = "VM agent offline";
                slot.OutOfGameStreak = 0;
                return false;
            }

            // A slot that exhausted its creates is not re-armed by the streak - that would just
            // reproduce the same failure a minute later. It gets one slow retry instead.
            if (slot.State == DcloneSlotState.Failed)
            {
                if (slot.FailedAtUtc is not { } failedAt
                    || nowUtc - failedAt < DcloneParkPolicy.FailedSlotRetryDelay)
                {
                    return false;
                }

                return BeginRepark(slot, "retrying after an earlier failure");
            }

            slot.OutOfGameStreak = DcloneParkPolicy.NextOutOfGameStreak(slot.OutOfGameStreak, presence);
            if (presence == DcloneParkPresence.Parked && slot.State == DcloneSlotState.Offline)
            {
                // It came back on its own, still holding the game it was given before it dropped.
                slot.State = DcloneSlotState.Parked;
                slot.Detail = "back online, still in its game";
            }

            return DcloneParkPolicy.ShouldRepark(slot.OutOfGameStreak)
                && BeginRepark(slot, "left its game; building a replacement");
        }
    }

    private static bool BeginRepark(Slot slot, string detail)
    {
        slot.ReparkInFlight = true;
        slot.OutOfGameStreak = 0;
        slot.Reparks++;
        slot.State = DcloneSlotState.Reparking;
        slot.GameName = null;
        slot.Password = null;
        slot.Detail = detail;
        slot.FailedAtUtc = null;
        return true;
    }

    /// <summary>
    /// Arms a slot that is not in a game yet for its first create, without waiting out the
    /// out-of-game streak a running park would have to accumulate.
    /// </summary>
    public bool TryBeginInitialPark(string accountKey)
    {
        lock (_sync)
        {
            var slot = Find(accountKey);
            if (slot is null || slot.ReparkInFlight)
            {
                return false;
            }

            slot.ReparkInFlight = true;
            slot.State = DcloneSlotState.Preparing;
            return true;
        }
    }

    public void EndParkAttempt(string accountKey)
    {
        Mutate(accountKey, slot => slot.ReparkInFlight = false);
    }

    public IReadOnlyList<DcloneParkSlotSnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _slots.Select(slot => new DcloneParkSlotSnapshot(
                slot.AccountKey,
                slot.DisplayName,
                slot.AgentId,
                slot.GameName,
                slot.Password,
                slot.State,
                slot.Detail,
                slot.Reparks)).ToArray();
        }
    }

    public int ParkedCount()
    {
        lock (_sync)
        {
            return _slots.Count(slot => slot.State == DcloneSlotState.Parked);
        }
    }

    public int SlotCount()
    {
        lock (_sync)
        {
            return _slots.Count;
        }
    }

    private void Mutate(string accountKey, Action<Slot> mutate)
    {
        lock (_sync)
        {
            if (Find(accountKey) is { } slot)
            {
                mutate(slot);
            }
        }
    }

    private Slot? Find(string accountKey)
    {
        return _slots.FirstOrDefault(
            slot => string.Equals(slot.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Slot
    {
        public Slot(string accountKey, string displayName, string agentId)
        {
            AccountKey = accountKey;
            DisplayName = displayName;
            AgentId = agentId;
        }

        public string AccountKey { get; }
        public string DisplayName { get; }
        public string AgentId { get; }
        public string? GameName { get; set; }
        public string? Password { get; set; }
        public DcloneSlotState State { get; set; } = DcloneSlotState.Preparing;
        public string Detail { get; set; } = "queued";
        public int Reparks { get; set; }
        public int OutOfGameStreak { get; set; }
        public bool ReparkInFlight { get; set; }
        public DateTimeOffset? FailedAtUtc { get; set; }
    }
}
