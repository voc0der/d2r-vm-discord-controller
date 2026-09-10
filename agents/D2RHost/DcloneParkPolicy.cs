using System.Text.Json;

namespace D2RHost;

/// <summary>
/// Where one parked bot sits right now, judged from a plain <c>status</c> read.
/// </summary>
internal enum DcloneParkPresence
{
    /// <summary>The VM agent is not connected, so nothing can be said and nothing can be done.</summary>
    Offline,

    /// <summary>Definitely inside its game.</summary>
    Parked,

    /// <summary>Definitely not inside a game: it dropped, crashed, or never got in.</summary>
    OutOfGame,

    /// <summary>A load screen or a degraded capture. Says nothing either way.</summary>
    NoEvidence
}

/// <summary>
/// The rules behind <c>/d2r dclone</c>: how many bots to park, how a park is judged from a status
/// read, and how patient the loop is before it tears a park down and rebuilds it.
/// </summary>
internal static class DcloneParkPolicy
{
    /// <summary>
    /// <c>status</c> is read-only and bypasses the agent's command gate (see VmOperations), so a
    /// short interval costs one screen classification per VM and never queues behind a create.
    /// It still has to be long enough that a client mid-load is not sampled twice inside the same
    /// load screen.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// D2R renders frames the classifier can only call <c>LobbyOrGame</c> - lobby and game share
    /// too much chrome to separate - and the pulse classifier this mirrors treats that as a
    /// definite "not in game". Requiring three consecutive out-of-game reads, a full minute at
    /// <see cref="CheckInterval"/>, keeps one such frame from tearing down a healthy park and
    /// abandoning a game name that has already been handed out.
    /// </summary>
    public const int ReparkAfterConsecutiveOutOfGameReads = 3;

    /// <summary>
    /// A create can fail because the minted name collided with a game that already exists. Every
    /// attempt mints a fresh name, so retrying is worth doing; past three the client itself is
    /// the problem and the slot is reported failed rather than retried on every poll.
    /// </summary>
    public const int MaxCreateAttempts = 3;

    /// <summary>
    /// How long a slot that exhausted its creates is left alone before the loop tries it again.
    /// A park runs for hours, so giving up on a VM permanently would quietly shrink the hunt; a
    /// slow retry still recovers a client that was merely mid-crash, without hammering one that
    /// is genuinely wedged (which is what /d2r status and a VM cycle are for).
    /// </summary>
    public static readonly TimeSpan FailedSlotRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many bots to park. Unlike follow-auto's 7, there is no cap from the game: every bot
    /// opens its OWN game, so the only limit is how many VMs are online. A smaller explicit
    /// <paramref name="requested"/> parks a subset and leaves the rest alone.
    /// </summary>
    public static int ResolveBotCount(int onlineAccounts, int? requested)
    {
        if (onlineAccounts <= 0)
        {
            return 0;
        }

        return requested is not { } wanted
            ? onlineAccounts
            : Math.Clamp(wanted, 1, onlineAccounts);
    }

    /// <summary>
    /// Mirrors VmOperations.ClassifyPulseInGame, which is the fleet's existing answer to "is this
    /// client in a game", but reads it off the status JSON the host already collects instead of
    /// spending a follow-auto pulse. Only screens D2R cannot possibly show from inside a game
    /// count as evidence of a drop.
    /// </summary>
    public static DcloneParkPresence Classify(bool connected, string? statusJson)
    {
        if (!connected)
        {
            return DcloneParkPresence.Offline;
        }

        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return DcloneParkPresence.NoEvidence;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return DcloneParkPresence.NoEvidence;
        }

        if (root.TryGetProperty("d2rRunning", out var running)
            && running.ValueKind == JsonValueKind.False)
        {
            return DcloneParkPresence.OutOfGame;
        }

        if (!root.TryGetProperty("d2rVisibleState", out var visible)
            || visible.ValueKind != JsonValueKind.String)
        {
            return DcloneParkPresence.NoEvidence;
        }

        return visible.GetString() switch
        {
            "InGame" => DcloneParkPresence.Parked,
            "LobbyOrGame"
                or "CharacterScreen"
                or "OfflineCharacterScreen"
                or "NotRunning"
                or "GraphicsDeviceFailure"
                or "GammaCalibration" => DcloneParkPresence.OutOfGame,
            // DiabloSplash is the load screen and Unknown is a capture the classifier could not
            // resolve. Both are what a client legitimately looks like on the way into a game.
            _ => DcloneParkPresence.NoEvidence
        };
    }

    /// <summary>
    /// Folds one presence reading into the consecutive out-of-game streak. Anything that is not
    /// evidence of a drop leaves the streak alone rather than resetting it: a bot that alternates
    /// load screen and character screen is still gone, and resetting on the load screen would
    /// make that bot never re-park.
    /// </summary>
    public static int NextOutOfGameStreak(int streak, DcloneParkPresence presence)
    {
        return presence switch
        {
            DcloneParkPresence.OutOfGame => streak + 1,
            DcloneParkPresence.Parked => 0,
            _ => streak
        };
    }

    public static bool ShouldRepark(int consecutiveOutOfGameReads)
    {
        return consecutiveOutOfGameReads >= ReparkAfterConsecutiveOutOfGameReads;
    }
}
