namespace D2RHost;

/// <summary>
/// Tracks the accounts the follow-auto host believes are in its current game, plus exact
/// accounts that must complete the normal menu recovery/join path before the all-joined
/// watcher may resume.
/// </summary>
internal sealed class FollowAutoAccountState
{
    private readonly HashSet<string> _joined = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recoveryPending = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Joined => _joined;

    public IReadOnlySet<string> RecoveryPending => _recoveryPending;

    public int JoinedCount => _joined.Count;

    public bool MarkJoined(string accountKey)
    {
        _recoveryPending.Remove(accountKey);
        return _joined.Add(accountKey);
    }

    public void BeginRecovery(string accountKey)
    {
        _joined.Remove(accountKey);
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

    public string[] BeginRecoveryForOfflineJoined(IReadOnlySet<string> onlineAccountKeys)
    {
        var offlineJoined = _joined
            .Where(accountKey => !onlineAccountKeys.Contains(accountKey))
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        BeginRecovery(offlineJoined);
        return offlineJoined;
    }

    public bool CanWatch(IReadOnlyCollection<string> onlineAccountKeys)
    {
        return onlineAccountKeys.Count > 0
            && _recoveryPending.Count == 0
            && _joined.SetEquals(onlineAccountKeys);
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
