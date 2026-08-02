namespace D2RHost;

/// <summary>
/// Tracks the accounts the follow-auto host believes are in its current game, plus exact
/// accounts that must complete the normal menu recovery/join path before the all-joined
/// watcher may resume, plus accounts parked because the current game reported full.
/// </summary>
internal sealed class FollowAutoAccountState
{
    /// <summary>
    /// Total "Game is full" reads an account may accumulate against one game before it parks:
    /// the initial detection plus three retries. A parked account stays warm at the lobby and
    /// deliberately never re-attempts the CURRENT game - if a human drops out of a full game,
    /// their slot must stay theirs, not get sniped by a bot. Parking clears when the fleet
    /// advances to the next game.
    /// </summary>
    public const int MaxGameFullAttempts = 4;

    private readonly HashSet<string> _joined = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recoveryPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _parkedGameFull = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gameFullStrikes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Joined => _joined;

    public IReadOnlySet<string> RecoveryPending => _recoveryPending;

    public IReadOnlySet<string> ParkedGameFull => _parkedGameFull;

    public int JoinedCount => _joined.Count;

    public int ParkedGameFullCount => _parkedGameFull.Count;

    /// <summary>
    /// Every account already committed to the current game - in it, recovering back into it, or
    /// parked holding its place. The roster keeps these accounts' slots when it recomputes, so a
    /// VM that comes online mid-run fills an empty slot instead of displacing a live client.
    /// </summary>
    public IReadOnlySet<string> Incumbents => _joined
        .Concat(_recoveryPending)
        .Concat(_parkedGameFull)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool MarkJoined(string accountKey)
    {
        _recoveryPending.Remove(accountKey);
        _parkedGameFull.Remove(accountKey);
        _gameFullStrikes.Remove(accountKey);
        return _joined.Add(accountKey);
    }

    /// <summary>
    /// Records one "Game is full" outcome for the current game. Returns the attempt number
    /// (1-based) and whether this strike parked the account. Parking removes the account from
    /// recovery-pending: a recovering client blocked by a full game has completed its recovery
    /// as far as the fleet is concerned and must not hold the all-joined watch hostage.
    /// </summary>
    public (int Attempt, bool Parked) RecordGameFullStrike(string accountKey)
    {
        _gameFullStrikes.TryGetValue(accountKey, out var priorAttempts);
        var attempt = priorAttempts + 1;
        _gameFullStrikes[accountKey] = attempt;
        if (attempt < MaxGameFullAttempts)
        {
            return (attempt, false);
        }

        _recoveryPending.Remove(accountKey);
        _parkedGameFull.Add(accountKey);
        return (attempt, true);
    }

    /// <summary>
    /// Un-parks every account and forgets all full-game strikes. Called when the current game
    /// ends (or is abandoned), because the next game is a fresh capacity situation. Returns
    /// the accounts that were parked so the monitor can name them.
    /// </summary>
    public string[] ClearGameFullParking()
    {
        var unparked = _parkedGameFull
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _parkedGameFull.Clear();
        _gameFullStrikes.Clear();
        return unparked;
    }

    public void BeginRecovery(string accountKey)
    {
        // Recovery supersedes full-game parking: the account must re-run the normal menu
        // recovery/join path. Its strikes survive, so if the game still reads full on the very
        // next attempt it re-parks immediately instead of getting a fresh retry budget.
        _joined.Remove(accountKey);
        _parkedGameFull.Remove(accountKey);
        _recoveryPending.Add(accountKey);
    }

    public void BeginRecovery(IEnumerable<string> accountKeys)
    {
        foreach (var accountKey in accountKeys)
        {
            BeginRecovery(accountKey);
        }
    }

    public void ClearJoined()
    {
        _joined.Clear();
    }

    /// <summary>
    /// Drops accounts the roster no longer wants from every piece of run state. Recovery-pending
    /// matters most: a benched account that stayed recovery-pending would block the all-joined
    /// watch forever, because nothing is going to drive its recovery once it is off the roster.
    /// </summary>
    public void Bench(IReadOnlySet<string> benchedAccountKeys)
    {
        foreach (var accountKey in benchedAccountKeys)
        {
            _joined.Remove(accountKey);
            _recoveryPending.Remove(accountKey);
            _parkedGameFull.Remove(accountKey);
            _gameFullStrikes.Remove(accountKey);
        }
    }

    public string[] BeginRecoveryForOfflineJoined(IReadOnlySet<string> onlineAccountKeys)
    {
        var offlineJoined = _joined
            .Where(accountKey => !onlineAccountKeys.Contains(accountKey))
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        BeginRecovery(offlineJoined);
        return offlineJoined;
    }

    /// <summary>
    /// The all-joined watch may run once every online, non-parked account is in the game and
    /// nothing is recovery-pending. Parked accounts are deliberately excluded from the joined
    /// comparison: they intentionally sit at the lobby for the rest of the current game, and
    /// requiring them would freeze the watch (and therefore game advancement - the only thing
    /// that un-parks them) forever.
    /// </summary>
    public bool CanWatch(IReadOnlyCollection<string> onlineAccountKeys)
    {
        var watchable = onlineAccountKeys
            .Where(accountKey => !_parkedGameFull.Contains(accountKey))
            .ToArray();
        return watchable.Length > 0
            && _recoveryPending.Count == 0
            && _joined.SetEquals(watchable);
    }

    public string[] GetOfflineRecoveryAccounts(IReadOnlySet<string> onlineAccountKeys)
    {
        return _recoveryPending
            .Where(accountKey => !onlineAccountKeys.Contains(accountKey))
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int CountExpectedAccounts(IReadOnlyCollection<string> onlineAccountKeys)
    {
        return onlineAccountKeys
            .Concat(_recoveryPending)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
