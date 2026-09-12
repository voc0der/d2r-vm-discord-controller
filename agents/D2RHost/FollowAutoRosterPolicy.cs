namespace D2RHost;

/// <summary>
/// Decides how many bots a follow-auto run should put in the leader's game, and which accounts
/// those are.
/// </summary>
/// <remarks>
/// <para>
/// Follow-auto used to send every online account into every game - the fleet size was the party
/// size. With more VMs than one player's game can hold, the run now carries a target instead: the
/// first <c>target</c> online accounts by key are the active roster, and the rest sit benched, warm
/// at the lobby, making no join attempts.
/// </para>
/// <para>
/// The target counts BOTS, not players. D2R caps a game at <see cref="MaxPlayersPerGame"/>, and the
/// leader being followed is a real player occupying one of those slots, so the default of
/// <see cref="DefaultBotCount"/> fills the game exactly. One press of -1 makes it a 7-player game,
/// and so on.
/// </para>
/// </remarks>
internal static class FollowAutoRosterPolicy
{
    public const int MaxPlayersPerGame = 8;

    /// <summary>Bots only - the leader holds the eighth slot.</summary>
    public const int MaxBotCount = MaxPlayersPerGame - 1;

    public const int MinBotCount = 1;

    public const int DefaultBotCount = MaxBotCount;

    /// <summary>
    /// Clamps a requested bot count to what a game can hold. Deliberately NOT clamped to how many
    /// accounts are currently online: VMs come and go (a power cycle, a node restart), and a target
    /// that silently shrank to match a temporary outage would never grow back on its own.
    /// </summary>
    public static int ClampTarget(int requested)
    {
        return Math.Clamp(requested, MinBotCount, MaxBotCount);
    }

    /// <summary>
    /// Splits online accounts and current-game incumbents into the ones that should hold roster
    /// slots and the ones that should sit this one out.
    /// </summary>
    /// <param name="incumbents">
    /// Accounts already committed to the current game - joined, recovering, or parked. They keep
    /// their slots.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is recomputed every cycle against whoever is online right now, so a worker node that
    /// connects after the run started contributes its VMs as soon as they are reachable: they are
    /// simply online accounts that were not online before, and they fill any slot the target has
    /// left open.
    /// </para>
    /// <para>
    /// Incumbents come first for exactly that reason. Ordering purely by key would let a
    /// late-arriving account that happens to sort earlier displace a bot that is already in the
    /// leader's game - kicking a live client to swap in an alphabetically luckier one. Within each
    /// group the order is by key, so the choice is stable run after run and -1 always benches the
    /// same predictable end of the list.
    /// </para>
    /// </remarks>
    public static FollowAutoRoster ResolveRoster(
        IEnumerable<string> onlineAccountKeys,
        int targetBotCount,
        IReadOnlySet<string>? incumbents = null)
    {
        var online = onlineAccountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incumbentSet = incumbents is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : incumbents.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = online
            .Where(incumbentSet.Contains)
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            // An offline recovery incumbent still occupies a requested roster slot. Putting it
            // after live incumbents preserves bots already in-game, while including it at all
            // lets a lower target bench/forget the excess recovery instead of waiting forever.
            .Concat(incumbentSet
                .Where(accountKey => !online.Contains(accountKey))
                .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase))
            .Concat(online
                .Where(accountKey => !incumbentSet.Contains(accountKey))
                .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var target = ClampTarget(targetBotCount);
        return new FollowAutoRoster(
            ordered.Take(target).ToArray(),
            ordered.Skip(target).ToArray(),
            target);
    }

    /// <summary>
    /// Whether +1 can do anything right now: there has to be a benched VM to promote, room under
    /// the game cap, and - when the player count is known - a free slot in the live game.
    /// </summary>
    /// <remarks>
    /// A null player count means no fleet account could read one (nobody in a game yet, or every
    /// sample degraded). That must not block growing the roster: with no game to be full of, the
    /// cap and the bench are the only real constraints.
    /// </remarks>
    public static bool CanAddBot(int targetBotCount, int connectedBenchedCount, int? livePlayerCount)
    {
        if (targetBotCount >= MaxBotCount || connectedBenchedCount <= 0)
        {
            return false;
        }

        return livePlayerCount is not { } players || players < MaxPlayersPerGame;
    }

    public static bool CanRemoveBot(int targetBotCount)
    {
        return targetBotCount > MinBotCount;
    }
}

/// <summary>
/// The accounts a follow-auto cycle should act on, and the ones it should leave alone.
/// </summary>
internal sealed record FollowAutoRoster(
    IReadOnlyList<string> Active,
    IReadOnlyList<string> Benched,
    int TargetBotCount)
{
    // Built once, not per access: these are read inside per-account filters, where a computed
    // property would allocate a fresh set for every candidate it tested.
    private readonly Lazy<IReadOnlySet<string>> _activeSet = new(
        () => Active.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private readonly Lazy<IReadOnlySet<string>> _benchedSet = new(
        () => Benched.ToHashSet(StringComparer.OrdinalIgnoreCase));

    public IReadOnlySet<string> ActiveSet => _activeSet.Value;

    public IReadOnlySet<string> BenchedSet => _benchedSet.Value;
}

/// <summary>
/// Atomic gateway-facing view of the roster the run loop actually resolved. The target tag is
/// part of the snapshot: a connected-bench count calculated for target 3 must never authorize a
/// second promotion after the gateway has already moved the target to 4.
/// </summary>
internal sealed record FollowAutoRosterAvailability(
    int TargetBotCount,
    int OnlineAccountCount,
    int ConnectedBenchedCount)
{
    public bool CanAddBot(int currentTargetBotCount, int? livePlayerCount)
    {
        return TargetBotCount == currentTargetBotCount
            && FollowAutoRosterPolicy.CanAddBot(
                currentTargetBotCount,
                ConnectedBenchedCount,
                livePlayerCount);
    }

    /// <summary>
    /// Carries a gateway target mutation forward until the run loop publishes an exact new
    /// roster. Promotions consume one known connected bench immediately; reductions leave the
    /// count conservative because the newly benched incumbent may currently be offline.
    /// </summary>
    public FollowAutoRosterAvailability AfterTargetAdjustment(int adjustedTargetBotCount)
    {
        var adjustedTarget = FollowAutoRosterPolicy.ClampTarget(adjustedTargetBotCount);
        var promoted = Math.Max(adjustedTarget - TargetBotCount, 0);
        return this with
        {
            TargetBotCount = adjustedTarget,
            ConnectedBenchedCount = Math.Max(ConnectedBenchedCount - promoted, 0)
        };
    }
}

/// <summary>
/// Rate limit for the -1 / +1 buttons. Each press moves a real client in or out of a live game, so
/// a double-click or an impatient operator must not queue up three joins at once.
/// </summary>
internal sealed class FollowAutoRosterAdjustmentGate
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private DateTimeOffset? _lastAdjustmentUtc;

    public bool TryAdjust(DateTimeOffset nowUtc, out TimeSpan retryAfter)
    {
        lock (_sync)
        {
            if (_lastAdjustmentUtc is { } last && nowUtc - last < MinimumInterval)
            {
                retryAfter = MinimumInterval - (nowUtc - last);
                return false;
            }

            _lastAdjustmentUtc = nowUtc;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Clears the throttle so a new run starts responsive.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _lastAdjustmentUtc = null;
        }
    }
}

/// <summary>
/// A stable snapshot of the inputs that selected the roster currently being watched. The
/// in-game watcher must yield as soon as either input changes so the outer loop can bench,
/// promote, or account for newly connected VMs without mistaking that change for a game end.
/// </summary>
internal sealed class FollowAutoRosterWatchSnapshot
{
    private readonly HashSet<string> _onlineAccountKeys;

    public FollowAutoRosterWatchSnapshot(int targetBotCount, IEnumerable<string> onlineAccountKeys)
    {
        TargetBotCount = FollowAutoRosterPolicy.ClampTarget(targetBotCount);
        _onlineAccountKeys = onlineAccountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public int TargetBotCount { get; }

    public bool RequiresReconciliation(int targetBotCount, IEnumerable<string> onlineAccountKeys)
    {
        return TargetBotCount != FollowAutoRosterPolicy.ClampTarget(targetBotCount)
            || !_onlineAccountKeys.SetEquals(onlineAccountKeys);
    }
}

/// <summary>
/// Thread-safe live bot target and party mode shared by the Discord gateway and follow run. Arming
/// a local host restart freezes both and returns the exact pair that must be journaled; therefore a
/// button press is either included in that snapshot or rejected, never acknowledged and lost.
/// </summary>
/// <remarks>
/// <para>
/// The mode lives here rather than beside it because the two cannot move independently: public mode
/// has a lower ceiling than private mode (<see cref="FollowAutoPublicModePolicy.MaxBotCount"/>), so
/// switching modes is also a target change, and a reader that saw the new mode against the old
/// target would offer a party size that mode cannot run.
/// </para>
/// <para>
/// The join hold (<see cref="FollowAutoJoinDelayPolicy"/>) rides along because it is read and
/// written by the same two tasks, but it is deliberately NOT part of that invariant: it constrains
/// neither the target nor the mode. It is also the one piece of run state here that is never
/// journaled - see <see cref="Reset"/>.
/// </para>
/// </remarks>
internal sealed class FollowAutoTargetControl
{
    private readonly object _sync = new();
    private int _targetBotCount = FollowAutoRosterPolicy.DefaultBotCount;
    private int _mode = (int)FollowAutoPartyMode.Private;
    private int _localRestartArmed;
    private int _joinDelayArmed;

    public int TargetBotCount => Volatile.Read(ref _targetBotCount);

    public FollowAutoPartyMode Mode => (FollowAutoPartyMode)Volatile.Read(ref _mode);

    public bool LocalRestartArmed => Volatile.Read(ref _localRestartArmed) != 0;

    /// <summary>
    /// Whether the fleet is holding back before entering a new game, to give the leader a head
    /// start at low monster density. Off unless somebody armed it during this run.
    /// </summary>
    public bool JoinDelayArmed => Volatile.Read(ref _joinDelayArmed) != 0;

    /// <summary>Reads both halves under the lock, for callers that must not mix generations.</summary>
    public (int TargetBotCount, FollowAutoPartyMode Mode) Snapshot
    {
        get
        {
            lock (_sync)
            {
                return (_targetBotCount, (FollowAutoPartyMode)_mode);
            }
        }
    }

    public static int ClampTargetForMode(int requested, FollowAutoPartyMode mode)
    {
        return mode == FollowAutoPartyMode.Public
            ? FollowAutoPublicModePolicy.ClampTarget(requested)
            : FollowAutoRosterPolicy.ClampTarget(requested);
    }

    /// <remarks>
    /// Every run start calls this, including the resume after a host recovery, and it is what keeps
    /// the join hold from outliving the run that armed it. The target and mode are handed in
    /// because they ARE carried across a restart; the hold deliberately is not, so it always starts
    /// off rather than quietly slowing down a fleet nobody asked to slow down.
    /// </remarks>
    public void Reset(int targetBotCount, FollowAutoPartyMode mode = FollowAutoPartyMode.Private)
    {
        lock (_sync)
        {
            Volatile.Write(ref _mode, (int)mode);
            Volatile.Write(ref _targetBotCount, ClampTargetForMode(targetBotCount, mode));
            Volatile.Write(ref _localRestartArmed, 0);
            Volatile.Write(ref _joinDelayArmed, 0);
        }
    }

    /// <summary>
    /// Switches party mode, carrying the current target into the new mode's range. Switching to
    /// public therefore trims a full-game target immediately, before any live sample arrives, so
    /// the promise the button makes is kept from the press rather than from the first pulse.
    /// </summary>
    public FollowAutoPartyModeChange TrySetMode(FollowAutoPartyMode mode)
    {
        lock (_sync)
        {
            var currentMode = (FollowAutoPartyMode)_mode;
            var currentTarget = _targetBotCount;
            if (_localRestartArmed != 0)
            {
                return new FollowAutoPartyModeChange(
                    FollowAutoPartyModeChangeOutcome.LocalRestartArmed,
                    currentMode,
                    currentMode,
                    currentTarget,
                    currentTarget);
            }

            if (currentMode == mode)
            {
                return new FollowAutoPartyModeChange(
                    FollowAutoPartyModeChangeOutcome.Unchanged,
                    currentMode,
                    currentMode,
                    currentTarget,
                    currentTarget);
            }

            var target = ClampTargetForMode(currentTarget, mode);
            // Only private mode holds, so a mode switch drops the hold rather than parking it.
            // Leaving the flag set would mean the hold came back by itself the next time someone
            // pressed Private - a control re-arming without a press.
            var joinDelayCleared = _joinDelayArmed != 0;
            Volatile.Write(ref _joinDelayArmed, 0);
            Volatile.Write(ref _mode, (int)mode);
            Volatile.Write(ref _targetBotCount, target);
            return new FollowAutoPartyModeChange(
                FollowAutoPartyModeChangeOutcome.Changed,
                currentMode,
                mode,
                currentTarget,
                target,
                joinDelayCleared);
        }
    }

    public FollowAutoTargetAdjustment TryAdjust(
        int delta,
        Func<int, bool> canAdjust)
    {
        lock (_sync)
        {
            var current = _targetBotCount;
            if (_localRestartArmed != 0)
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.LocalRestartArmed,
                    current,
                    current);
            }

            if (!canAdjust(current))
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.Refused,
                    current,
                    current);
            }

            var target = ClampTargetForMode(current + delta, (FollowAutoPartyMode)_mode);
            if (target == current)
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.AtLimit,
                    current,
                    current);
            }

            Volatile.Write(ref _targetBotCount, target);
            return new FollowAutoTargetAdjustment(
                FollowAutoTargetAdjustmentOutcome.Changed,
                current,
                target);
        }
    }

    /// <summary>
    /// Sets the target on behalf of a rule that only owns it while a particular mode is in force -
    /// public mode's live player count, today. The mode is rechecked inside the lock because the
    /// caller reads it, decides, and writes across three separate operations on a different task
    /// from the gateway: without this, a Private press landing in that window is acknowledged and
    /// then immediately overwritten by the derived target it was meant to stop.
    /// </summary>
    public FollowAutoTargetAdjustment TrySetTargetForMode(int target, FollowAutoPartyMode expectedMode)
    {
        lock (_sync)
        {
            var current = _targetBotCount;
            if (_localRestartArmed != 0)
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.LocalRestartArmed,
                    current,
                    current);
            }

            if ((FollowAutoPartyMode)_mode != expectedMode)
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.Refused,
                    current,
                    current);
            }

            var clamped = ClampTargetForMode(target, expectedMode);
            if (clamped == current)
            {
                return new FollowAutoTargetAdjustment(
                    FollowAutoTargetAdjustmentOutcome.AtLimit,
                    current,
                    current);
            }

            Volatile.Write(ref _targetBotCount, clamped);
            return new FollowAutoTargetAdjustment(
                FollowAutoTargetAdjustmentOutcome.Changed,
                current,
                clamped);
        }
    }

    /// <summary>
    /// Flips the join hold, from the live monitor's delay button.
    /// </summary>
    /// <remarks>
    /// Refused outside private mode even though the monitor stops rendering the button there: the
    /// message ID does not change across that edit, so a client still showing the pre-switch
    /// components can land a press here. Refused while a local restart is armed for the same reason
    /// the bot count and party mode are - the run state has been journaled, and a press accepted
    /// after that snapshot would vanish when the host came back.
    /// </remarks>
    public FollowAutoJoinDelayChange TryToggleJoinDelay()
    {
        lock (_sync)
        {
            if (_localRestartArmed != 0)
            {
                return new FollowAutoJoinDelayChange(
                    FollowAutoJoinDelayChangeOutcome.LocalRestartArmed,
                    _joinDelayArmed != 0);
            }

            if ((FollowAutoPartyMode)_mode != FollowAutoPartyMode.Private)
            {
                return new FollowAutoJoinDelayChange(
                    FollowAutoJoinDelayChangeOutcome.NotPrivateMode,
                    _joinDelayArmed != 0);
            }

            var armed = _joinDelayArmed == 0;
            Volatile.Write(ref _joinDelayArmed, armed ? 1 : 0);
            return new FollowAutoJoinDelayChange(FollowAutoJoinDelayChangeOutcome.Changed, armed);
        }
    }

    public (int TargetBotCount, FollowAutoPartyMode Mode) ArmLocalRestart()
    {
        lock (_sync)
        {
            Volatile.Write(ref _localRestartArmed, 1);
            return (_targetBotCount, (FollowAutoPartyMode)_mode);
        }
    }

    public void DisarmLocalRestart()
    {
        lock (_sync)
        {
            Volatile.Write(ref _localRestartArmed, 0);
        }
    }
}

internal enum FollowAutoPartyModeChangeOutcome
{
    Changed,
    Unchanged,
    LocalRestartArmed
}

internal sealed record FollowAutoPartyModeChange(
    FollowAutoPartyModeChangeOutcome Outcome,
    FollowAutoPartyMode PreviousMode,
    FollowAutoPartyMode Mode,
    int PreviousTarget,
    int Target,
    bool JoinDelayCleared = false);

internal enum FollowAutoJoinDelayChangeOutcome
{
    Changed,
    NotPrivateMode,
    LocalRestartArmed
}

internal sealed record FollowAutoJoinDelayChange(
    FollowAutoJoinDelayChangeOutcome Outcome,
    bool Armed);

internal enum FollowAutoTargetAdjustmentOutcome
{
    Changed,
    Refused,
    AtLimit,
    LocalRestartArmed
}

internal sealed record FollowAutoTargetAdjustment(
    FollowAutoTargetAdjustmentOutcome Outcome,
    int PreviousTarget,
    int Target);

/// <summary>
/// Highest player count observed in the current game. A later low or unreadable sample must not
/// reopen +1 after the game was known full; only confirmed advancement resets the high-water.
/// </summary>
internal sealed class FollowAutoPlayerCountHighWater
{
    private const int Unknown = -1;
    private int _highestOrUnknown = Unknown;

    public int? Value
    {
        get
        {
            var value = Volatile.Read(ref _highestOrUnknown);
            return value == Unknown ? null : value;
        }
    }

    public void Observe(int? playerCount, bool fresh = true)
    {
        if (!fresh || playerCount is not { } observed)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _highestOrUnknown);
            if (current >= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _highestOrUnknown, observed, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Releases capacity after this controller positively confirms that rostered bots left the
    /// current game. A subsequent fresh sample can raise the value again if someone else fills a
    /// slot; cached fallback counts cannot undo the confirmed departure.
    /// </summary>
    public void RecordConfirmedDepartures(int count)
    {
        if (count <= 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _highestOrUnknown);
            if (current == Unknown)
            {
                return;
            }

            var adjusted = Math.Max(current - count, 0);
            if (Interlocked.CompareExchange(ref _highestOrUnknown, adjusted, current) == current)
            {
                return;
            }
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _highestOrUnknown, Unknown);
    }
}
