namespace D2RAgent;

/// <summary>
/// Bounds the reselect-and-retry loop inside
/// <c>ClickFriendJoinOptionUntilEnteredGameAsync</c> so a client that keeps bouncing off game
/// entry returns a real answer instead of being killed by the agent-side command timeout.
/// </summary>
/// <remarks>
/// <para>
/// Every retry path in that loop reselects the friend game with <c>resetDeadline: true</c>, which
/// pushes the per-attempt deadline forward by <c>gameEntryStartTimeoutSeconds</c> again. That is
/// correct for a single retry - a fresh submit deserves a fresh window - but nothing capped how
/// many times it could happen. A client caught in an error-dialog or connection-interrupt storm
/// therefore renewed its own deadline indefinitely and ran until the host's 205s agent-side
/// timeout guillotined the whole <c>menu_follow_auto_check</c> command.
/// </para>
/// <para>
/// That was the worst possible ending. A timeout is reported as <c>ok=false</c>, which the host
/// reads as a bare check failure carrying no join diagnosis at all: no dialog counts, no wait
/// result, nothing to escalate on. The bot sat at the lobby, the host logged "checks could not
/// complete", and the cycle repeated every ~3.5 minutes indefinitely. Returning a normal
/// <c>Waiting</c> result a minute earlier tells the host far more than the timeout ever did.
/// </para>
/// <para>
/// Two independent limits, because they fail differently. The per-reason caps stop one specific
/// pathology from monopolizing the budget, and the overall ceiling stops any mix of them - or a
/// single pathologically slow attempt - from reaching the command timeout.
/// </para>
/// </remarks>
internal sealed class FriendJoinRetryBudget
{
    /// <summary>Stale/error dialogs dismissed and reselected before giving up on this cycle.</summary>
    internal const int MaxDialogRetries = 3;

    /// <summary>"Connection Interrupted" reselects before giving up on this cycle.</summary>
    internal const int MaxConnectionRetries = 3;

    /// <summary>
    /// Retries across every reason combined, including the menu/character-screen returns that
    /// have no per-reason cap of their own.
    /// </summary>
    internal const int MaxTotalRetries = 6;

    /// <summary>
    /// The wall-clock ceiling for the whole loop. Well under the 205s agent-side timeout that a
    /// 210s host command negotiates, so the loop always gets to return its own diagnosis.
    /// </summary>
    internal static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(150);

    private readonly DateTimeOffset _startedUtc;
    private readonly TimeSpan _overallBudget;

    public FriendJoinRetryBudget(DateTimeOffset startedUtc, TimeSpan? overallBudget = null)
    {
        _startedUtc = startedUtc;
        _overallBudget = overallBudget ?? OverallBudget;
    }

    public int DialogRetries { get; private set; }

    public int ConnectionRetries { get; private set; }

    public int TotalRetries { get; private set; }

    public DateTimeOffset HardDeadlineUtc => _startedUtc + _overallBudget;

    /// <summary>
    /// A renewed per-attempt deadline may never outlive the overall budget, so
    /// <c>resetDeadline</c> can extend the loop but never escape it.
    /// </summary>
    public DateTimeOffset ClampDeadline(DateTimeOffset requestedDeadlineUtc)
    {
        var hard = HardDeadlineUtc;
        return requestedDeadlineUtc > hard ? hard : requestedDeadlineUtc;
    }

    public bool IsExhausted(DateTimeOffset utcNow)
    {
        return utcNow >= HardDeadlineUtc || TotalRetries >= MaxTotalRetries;
    }

    /// <summary>
    /// Records one retry of the given kind and reports whether the loop may continue. A refusal
    /// is a normal, reportable outcome - not an error - so the caller returns its usual
    /// "still waiting" result and lets the host decide what happens next.
    /// </summary>
    public bool TryConsume(FriendJoinRetryReason reason, DateTimeOffset utcNow, out string exhaustedReason)
    {
        switch (reason)
        {
            case FriendJoinRetryReason.ErrorDialog:
                DialogRetries++;
                break;
            case FriendJoinRetryReason.ConnectionInterrupted:
                ConnectionRetries++;
                break;
        }

        TotalRetries++;

        // Counters are incremented before the check so the returned result still reports the
        // attempt that hit the wall; the operator needs to see 4 dialogs, not 3.
        if (reason == FriendJoinRetryReason.ErrorDialog && DialogRetries > MaxDialogRetries)
        {
            exhaustedReason = $"dismissed {DialogRetries} game-entry error dialogs without entering the game";
            return false;
        }

        if (reason == FriendJoinRetryReason.ConnectionInterrupted && ConnectionRetries > MaxConnectionRetries)
        {
            exhaustedReason = $"the connection was interrupted {ConnectionRetries} times without entering the game";
            return false;
        }

        if (TotalRetries > MaxTotalRetries)
        {
            exhaustedReason = $"reselected the friend game {TotalRetries} times without entering the game";
            return false;
        }

        if (utcNow >= HardDeadlineUtc)
        {
            exhaustedReason =
                $"spent the whole {_overallBudget.TotalSeconds:N0}s game-entry budget without entering the game";
            return false;
        }

        exhaustedReason = string.Empty;
        return true;
    }
}

internal enum FriendJoinRetryReason
{
    ErrorDialog,
    ConnectionInterrupted,
    ReturnedToMenu,
    ReturnedToCharacterScreen,
    OfflineCharacterScreen
}
