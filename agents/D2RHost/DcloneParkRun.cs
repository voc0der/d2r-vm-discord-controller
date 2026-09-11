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

internal enum DcloneParkCancelOutcome
{
    NotRunning,

    /// <summary>The control was pressed on a monitor that belongs to an earlier park.</summary>
    StaleMonitor,

    Stopped
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
///
/// The roster grows while the run is live: VMs that connect after the start are admitted and
/// parked too, up to <see cref="MaxBots"/> when one was given.
/// </remarks>
internal sealed class DcloneParkRun
{
    private readonly object _sync = new();
    private readonly HashSet<string> _spentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Slot> _slots = new();
    private string? _stopReason;
    private bool _handedOff;
    private DateTimeOffset _nextInitialParkUtc = DateTimeOffset.MinValue;

    public DcloneParkRun(long runId, string difficulty, DateTimeOffset startedUtc, int? maxBots = null)
    {
        RunId = runId;
        Difficulty = difficulty;
        StartedUtc = startedUtc;
        MaxBots = maxBots;
    }

    public long RunId { get; }
    public string Difficulty { get; }
    public DateTimeOffset StartedUtc { get; }

    /// <summary>The explicit <c>bots</c> cap on the roster, or null to park every VM that connects.</summary>
    public int? MaxBots { get; }

    /// <summary>
    /// True when the park ended because the operator switched the fleet to follow-auto. The
    /// finished monitor then must not offer Leave/Quit: those act on the whole fleet, which the
    /// successor mode now owns.
    /// </summary>
    public bool HandedOff
    {
        get
        {
            lock (_sync)
            {
                return _handedOff;
            }
        }
    }

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

    public void RecordStopReason(string? reason, bool handedOff = false)
    {
        lock (_sync)
        {
            if (_stopReason is null && reason is not null)
            {
                _stopReason = reason;
                _handedOff = handedOff;
            }
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
    /// Adds a slot for every online account the park does not hold yet, within
    /// <see cref="MaxBots"/>, and returns the keys it added. Each one still needs its first create.
    /// </summary>
    public IReadOnlyList<string> AdmitNewcomers(
        IReadOnlyList<(string AccountKey, string DisplayName, string AgentId)> online)
    {
        lock (_sync)
        {
            var admitted = DcloneParkPolicy.SelectNewcomers(
                online.Select(candidate => candidate.AccountKey),
                _slots.Select(slot => slot.AccountKey),
                MaxBots);
            foreach (var accountKey in admitted)
            {
                var candidate = online.First(entry =>
                    string.Equals(entry.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
                _slots.Add(new Slot(candidate.AccountKey, candidate.DisplayName, candidate.AgentId));
            }

            return admitted;
        }
    }

    /// <summary>
    /// How long a newly admitted slot waits before its first create, so that first creates stay
    /// <paramref name="stagger"/> apart across the whole run rather than only within one batch. A
    /// node's VMs connect a few seconds apart and would otherwise land in separate sweeps that each
    /// start at zero, all driving Battle.net at the same instant.
    /// </summary>
    public TimeSpan ReserveInitialParkDelay(DateTimeOffset nowUtc, TimeSpan stagger)
    {
        lock (_sync)
        {
            var startAt = _nextInitialParkUtc > nowUtc ? _nextInitialParkUtc : nowUtc;
            _nextInitialParkUtc = startAt + (stagger > TimeSpan.Zero ? stagger : TimeSpan.Zero);
            return startAt - nowUtc;
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
            slot.PreviousGameName = null;
            slot.PreviousPassword = null;
            slot.Detail = detail;
            slot.OutOfGameStreak = 0;
            slot.ReparkInFlight = false;
            slot.AwaitingInitialPark = false;
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
            slot.PreviousGameName = null;
            slot.PreviousPassword = null;
            slot.Detail = detail;
            slot.OutOfGameStreak = 0;
            slot.ReparkInFlight = false;
            slot.AwaitingInitialPark = false;
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

            // A newcomer still waiting out its stagger has a first create queued. Arming a rebuild
            // here would race it: whichever finished first, the other would then create a second
            // game on the same bot.
            if (slot.AwaitingInitialPark)
            {
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
                if (slot.GameName is null)
                {
                    // It dropped mid-create and came back inside a game this run never recorded a
                    // name for. Nobody can be handed that game, so it is rebuilt under a known one
                    // rather than listed as parked with a blank name and password.
                    return BeginRepark(slot, "back online in a game with no recorded name; rebuilding");
                }

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
        // Kept aside rather than discarded, for TryRestoreMisreadPark.
        slot.PreviousGameName = slot.GameName;
        slot.PreviousPassword = slot.Password;
        slot.GameName = null;
        slot.Password = null;
        slot.Detail = detail;
        slot.FailedAtUtc = null;
        return true;
    }

    /// <summary>
    /// Undoes a rebuild whose bot turns out to still be in the game it was parked in, and answers
    /// whether it did.
    /// </summary>
    /// <remarks>
    /// A rebuild is armed by three out-of-game readings, and <c>LobbyOrGame</c> - which counts as
    /// one - is a frame D2R can also render from inside a game. A bot cannot enter a game on its
    /// own, so when the rebuild's own first reading finds it in one, the drop was a misread and the
    /// game it is in is the one whose name is already circulating. Leaving it to create a new one
    /// would tear down exactly the game people are joining. Only valid before this rebuild has sent
    /// any create; after that the bot may be in the new game instead.
    /// </remarks>
    public bool TryRestoreMisreadPark(string accountKey)
    {
        lock (_sync)
        {
            var slot = Find(accountKey);
            if (slot?.PreviousGameName is not { } gameName || slot.PreviousPassword is not { } password)
            {
                return false;
            }

            slot.State = DcloneSlotState.Parked;
            slot.GameName = gameName;
            slot.Password = password;
            slot.PreviousGameName = null;
            slot.PreviousPassword = null;
            slot.Detail = "still in its game; the drop was a misread";
            slot.OutOfGameStreak = 0;
            slot.Reparks = Math.Max(slot.Reparks - 1, 0);
            return true;
        }
    }

    /// <summary>
    /// Arms a newly admitted slot for its first create, without waiting out the out-of-game streak
    /// a running park would have to accumulate. Only ever succeeds once per slot: after that the
    /// sweep owns every rebuild.
    /// </summary>
    public bool TryBeginInitialPark(string accountKey)
    {
        lock (_sync)
        {
            var slot = Find(accountKey);
            if (slot is null || slot.ReparkInFlight || !slot.AwaitingInitialPark)
            {
                return false;
            }

            slot.AwaitingInitialPark = false;
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
        public string? PreviousGameName { get; set; }
        public string? PreviousPassword { get; set; }
        public DcloneSlotState State { get; set; } = DcloneSlotState.Preparing;
        public string Detail { get; set; } = "queued";
        public int Reparks { get; set; }
        public int OutOfGameStreak { get; set; }
        public bool ReparkInFlight { get; set; }
        public bool AwaitingInitialPark { get; set; } = true;
        public DateTimeOffset? FailedAtUtc { get; set; }
    }
}
