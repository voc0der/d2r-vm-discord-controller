namespace D2RHost;

/// <summary>
/// Which rule decides how many bots a follow-auto run puts in the leader's game.
/// </summary>
internal enum FollowAutoPartyMode
{
    /// <summary>
    /// The operator owns the bot count: it is whatever the run started with, moved by the live
    /// monitor's -1 / +1 buttons. This is how follow-auto has always worked.
    /// </summary>
    Private,

    /// <summary>
    /// The fleet holds the game one slot short of the cap so a real player can always walk in.
    /// The bot count is derived from the live player count every pulse instead of being set by
    /// hand, so bots step aside as humans arrive and come back as humans leave.
    /// </summary>
    Public
}

/// <summary>
/// Public mode's arithmetic: how many bots belong in a game that must always keep a seat free.
/// </summary>
/// <remarks>
/// <para>
/// Private mode fills the game - the operator's chosen count plus the leader, up to a full eight.
/// Public mode instead treats the party size as an output: whatever the live player count is, the
/// fleet trims itself until exactly <see cref="ReservedSlots"/> slot is still open. Five humans
/// therefore get two bots, four humans get three, and as more humans arrive the bots leave to keep
/// the gap.
/// </para>
/// <para>
/// The leader being followed is a real player and always occupies one of the target's slots, so
/// public mode can never want more than <see cref="MaxBotCount"/> bots. That ceiling is structural,
/// not a preference, and it is applied the moment the mode is switched on rather than waiting for
/// the first live sample.
/// </para>
/// </remarks>
internal static class FollowAutoPublicModePolicy
{
    /// <summary>Slots public mode refuses to fill, so a real player can always join.</summary>
    public const int ReservedSlots = 1;

    /// <summary>Total players public mode aims for - the game cap minus the reserved slot.</summary>
    public const int TargetPlayerCount = FollowAutoRosterPolicy.MaxPlayersPerGame - ReservedSlots;

    /// <summary>The most bots public mode can ever want, because the leader is one of the humans.</summary>
    public const int MaxBotCount = TargetPlayerCount - 1;

    /// <summary>
    /// The floor is one bot, not zero, and it is a hard technical limit rather than a policy
    /// choice: everything follow-auto knows about the leader's game - the player count, whether the
    /// bound nametag is still in the party bar, whether the game ended at all - is read off a
    /// client that is inside it. With no client in the game the run is blind and can never bring
    /// itself back, which is the same dead end the all-parked-on-a-full-game stall already
    /// documents. So at <see cref="TargetPlayerCount"/> humans the fleet keeps exactly one client,
    /// the game runs full, and the monitor says so instead of pretending the gap is held.
    /// </summary>
    public const int MinBotCount = FollowAutoRosterPolicy.MinBotCount;

    /// <summary>
    /// Fresh samples that must agree before the fleet gives a slot back. Deliberately lower than
    /// <see cref="ConfirmationsToClaim"/>: yielding on a bad read costs one bot a game, while
    /// claiming on a bad read costs a real player the seat the whole mode exists to protect.
    /// </summary>
    public const int ConfirmationsToYield = 2;

    public const int ConfirmationsToClaim = 3;

    /// <summary>
    /// The bot count that leaves exactly one slot open alongside <paramref name="humanCount"/> real
    /// players, clamped to what public mode can actually run.
    /// </summary>
    public static int ResolveTargetBotCount(int humanCount)
    {
        return ClampTarget(TargetPlayerCount - humanCount);
    }

    public static int ClampTarget(int requested)
    {
        return Math.Clamp(requested, MinBotCount, MaxBotCount);
    }

    /// <summary>
    /// Splits an observed party size into the humans in it. Null whenever the sample cannot say -
    /// a vantage that is loading, at the menus, or reporting a cached count is not evidence about
    /// the current game, and public mode must not act on it.
    /// </summary>
    /// <param name="botsInGame">
    /// Fleet clients the run believes are inside the sampled game. The sampling client counts
    /// itself in <paramref name="playerCount"/>, so this is subtracted whole.
    /// </param>
    public static int? CountHumans(int? playerCount, bool fresh, bool? inGame, int botsInGame)
    {
        if (!fresh || inGame == false || playerCount is not { } players || players < 1)
        {
            return null;
        }

        return Math.Max(players - Math.Max(botsInGame, 0), 0);
    }

    /// <summary>Whether the fleet is holding the game full because it cannot go below one client.</summary>
    public static bool IsHoldingLastVantage(int targetBotCount, int? humanCount)
    {
        return targetBotCount <= MinBotCount
            && humanCount is { } humans
            && humans >= TargetPlayerCount;
    }
}

/// <summary>
/// Turns follow-auto's in-game pulses into a public-mode bot target, once enough fresh samples
/// agree on the same answer.
/// </summary>
/// <remarks>
/// <para>
/// Only samples taken while every rostered bot is already in the game are worth anything here. Mid
/// join the party bar lags the roster - four clients confirmed joined while two are still on a
/// loading screen reads as a smaller party than there is - and subtracting the roster from that
/// undercounts the humans, which would raise the target and fill the very slot the mode is holding
/// open. The all-joined watch is the only caller for exactly that reason.
/// </para>
/// <para>
/// Even there a single sample is not enough: one degraded capture that under-reads the party bar
/// would look like humans leaving. A target is only handed back after the same answer repeats,
/// asymmetrically (see <see cref="FollowAutoPublicModePolicy.ConfirmationsToYield"/>), and a
/// disagreeing sample restarts the streak.
/// </para>
/// </remarks>
internal sealed class FollowAutoPublicModeTracker
{
    private readonly object _sync = new();
    private int? _candidate;
    private int _agreements;
    private int? _lastHumanCount;
    private int? _lastPlayerCount;

    /// <summary>Humans in the last usable sample, for the monitor. Null until one lands.</summary>
    public int? LastHumanCount
    {
        get
        {
            lock (_sync)
            {
                return _lastHumanCount;
            }
        }
    }

    public int? LastPlayerCount
    {
        get
        {
            lock (_sync)
            {
                return _lastPlayerCount;
            }
        }
    }

    /// <summary>
    /// Drops the streak and the last reading when the fleet leaves for a new game. The target
    /// itself deliberately carries over: it is the best prior available for the leader's next
    /// game, and re-deriving it from scratch would trickle the fleet back in one client at a time
    /// on every single game.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _candidate = null;
            _agreements = 0;
            _lastHumanCount = null;
            _lastPlayerCount = null;
        }
    }

    /// <summary>
    /// Records one pulse and returns the target the run should now apply, or null for "keep
    /// watching". Unusable samples neither advance nor reset the streak - a flaky vantage should
    /// slow the decision down, not permanently block it.
    /// </summary>
    public int? Observe(int? playerCount, bool fresh, bool? inGame, int botsInGame, int currentTarget)
    {
        var humans = FollowAutoPublicModePolicy.CountHumans(playerCount, fresh, inGame, botsInGame);
        if (humans is not { } humanCount)
        {
            return null;
        }

        var desired = FollowAutoPublicModePolicy.ResolveTargetBotCount(humanCount);
        lock (_sync)
        {
            _lastHumanCount = humanCount;
            _lastPlayerCount = playerCount;
            if (_candidate == desired)
            {
                _agreements++;
            }
            else
            {
                _candidate = desired;
                _agreements = 1;
            }

            if (desired == currentTarget)
            {
                return null;
            }

            var required = desired < currentTarget
                ? FollowAutoPublicModePolicy.ConfirmationsToYield
                : FollowAutoPublicModePolicy.ConfirmationsToClaim;
            // The streak is deliberately not cleared on the way out. If the run refuses this
            // change (the shared adjustment throttle, an armed host restart), the next pulse
            // re-offers it immediately instead of starting the count over.
            return _agreements >= required ? desired : null;
        }
    }
}
