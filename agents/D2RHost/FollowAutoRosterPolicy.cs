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
    /// Splits the online accounts into the ones that should be in the game and the ones that should
    /// sit this one out.
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
        var ordered = onlineAccountKeys
            .OrderByDescending(accountKey => incumbents?.Contains(accountKey) == true)
            .ThenBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
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
    public static bool CanAddBot(int targetBotCount, int onlineAccountCount, int? livePlayerCount)
    {
        if (targetBotCount >= MaxBotCount || targetBotCount >= onlineAccountCount)
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
