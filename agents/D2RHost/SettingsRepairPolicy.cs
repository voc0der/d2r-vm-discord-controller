using System.Text.Json;

namespace D2RHost;

/// <summary>
/// Decides when a VM's Settings.json needs replacing from the fleet, which VM should donate the
/// replacement, and how often either may be attempted.
/// </summary>
/// <remarks>
/// D2R regenerates its own Settings.json from defaults whenever it decides the file is unusable
/// (a guest losing power mid-write is the leading suspect) and then stops on its first-run gamma
/// calibration screen instead of reaching character select. The client never leaves that screen on
/// its own, so the existing warmup escalation - power-cycle the VM, then restart the whole physical
/// node - spends both on a problem neither can fix. Every VM in this fleet runs the same client
/// configuration, so a healthy sibling's file is the repair.
/// </remarks>
internal static class SettingsRepairPolicy
{
    /// <summary>
    /// True when an agent's status (or a failed command's status payload) says the client is
    /// confirmed to be on the gamma calibration screen and is asking for a donor settings file.
    /// Agents that predate the field, and agents with repair disabled, read false.
    /// </summary>
    public static bool NeedsDonorSettings(string? statusJson)
    {
        return TryReadRepairFlag(statusJson, "needsDonorSettings");
    }

    /// <summary>
    /// True when the client is on the gamma screen at all, confirmed or not. Used for reporting and
    /// for keeping a VM out of the donor pool - never for deciding to overwrite a settings file.
    /// </summary>
    public static bool IsSettingsCorrupt(string? statusJson)
    {
        return TryReadRepairFlag(statusJson, "detected");
    }

    /// <summary>
    /// How much evidence a candidate's own client gives that its settings file actually works.
    /// </summary>
    public enum DonorHealth
    {
        /// <summary>No evidence its settings are good, or evidence they are not. Never a donor.</summary>
        Unusable,

        /// <summary>Logged in with its character list rendered - it got to character select and past login.</summary>
        ReachedCharacterScreen,

        /// <summary>In the lobby or in a game - it got all the way past character select on these settings.</summary>
        ReachedLobbyOrGame
    }

    /// <summary>
    /// Grades a candidate on what its client is visibly doing. Being a donor requires positive proof
    /// that the settings work, not merely the absence of the gamma screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything short of character select is treated as broken, not merely unknown: a client
    /// sitting on the splash, on an intro frame, on the graphics-device dialog, or on an
    /// unrecognizable frame is a client that has not started successfully, and a client that has not
    /// started successfully is no evidence that the file it started from is good. That covers the
    /// case this whole path exists for - the corrupt VM itself spends a while looking like a plain
    /// unrecognized frame on its way to the gamma screen.
    /// </para>
    /// <para>
    /// <c>OfflineCharacterScreen</c> is excluded for the same reason one step later: the client
    /// reached character select but with an empty character list, meaning login never completed.
    /// </para>
    /// <para>
    /// <c>NotRunning</c> is Unusable too, even though its settings file is sitting right there and
    /// readable. A closed client has proven nothing, and the whole point of a donor is that some
    /// client somewhere started successfully with that exact file.
    /// </para>
    /// </remarks>
    public static DonorHealth ClassifyDonor(string? statusJson)
    {
        if (IsSettingsCorrupt(statusJson) || string.IsNullOrWhiteSpace(statusJson))
        {
            return DonorHealth.Unusable;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (!document.RootElement.TryGetProperty("d2rVisibleState", out var visible)
                || visible.ValueKind != JsonValueKind.String)
            {
                return DonorHealth.Unusable;
            }

            return visible.GetString() switch
            {
                "LobbyOrGame" or "InGame" => DonorHealth.ReachedLobbyOrGame,
                "CharacterScreen" => DonorHealth.ReachedCharacterScreen,
                // OfflineCharacterScreen (stuck at character select, never logged in),
                // GammaCalibration, GraphicsDeviceFailure, DiabloSplash, Unknown, NotRunning.
                _ => DonorHealth.Unusable
            };
        }
        catch (JsonException)
        {
            return DonorHealth.Unusable;
        }
    }

    /// <summary>
    /// Orders donor candidates: an explicitly configured donor first if it is eligible, then the
    /// clients with the strongest evidence their settings work (lobby/in-game before character
    /// screen), then by key. The broken account is never a candidate, and neither is any client
    /// that is itself corrupt, stuck at character select, or not visibly running.
    /// </summary>
    public static IReadOnlyList<string> SelectDonorOrder(
        string brokenAccountKey,
        IEnumerable<SettingsDonorCandidate> candidates,
        string? preferredDonorAccountKey)
    {
        var eligible = candidates
            .Where(candidate => candidate.Connected)
            .Where(candidate => !string.Equals(candidate.AccountKey, brokenAccountKey, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => (candidate.AccountKey, Health: ClassifyDonor(candidate.StatusJson)))
            .Where(candidate => candidate.Health != DonorHealth.Unusable)
            // An explicitly configured donor outranks the health ordering, but still has to be
            // eligible - the operator picked which healthy client to prefer, not whether to
            // copy from a broken one.
            .OrderByDescending(candidate => string.Equals(
                candidate.AccountKey, preferredDonorAccountKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.Health)
            .ThenBy(candidate => candidate.AccountKey, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.AccountKey)
            .ToList();

        return eligible;
    }

    private static bool TryReadRepairFlag(string? statusJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (!document.RootElement.TryGetProperty("d2rSettingsRepair", out var repair)
                || repair.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return repair.TryGetProperty(propertyName, out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal readonly record struct SettingsDonorCandidate(
    string AccountKey,
    bool Connected,
    string? StatusJson);

/// <summary>
/// Rate-limits settings repairs per account. A repair closes a live client and overwrites a file,
/// so a client that keeps landing back on the gamma screen must not turn into a repair loop - after
/// <see cref="MaxAttemptsPerIncident"/> tries the account is left alone until the cooldown lapses,
/// and the ordinary warmup escalation takes over.
/// </summary>
internal sealed class SettingsRepairTracker
{
    internal const int MaxAttemptsPerIncident = 2;
    internal static readonly TimeSpan IncidentWindow = TimeSpan.FromMinutes(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, AccountRepairState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool TryBeginRepair(string accountKey, DateTimeOffset nowUtc, out string blockedReason)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (_attempts.TryGetValue(accountKey, out var state)
                && nowUtc - state.LastAttemptUtc < IncidentWindow)
            {
                if (state.Attempts >= MaxAttemptsPerIncident)
                {
                    var retryAt = state.LastAttemptUtc + IncidentWindow;
                    blockedReason = $"already replaced this account's settings {state.Attempts} time(s) since "
                        + $"{state.FirstAttemptUtc:HH:mm:ss}Z and it came back corrupt; not trying again before "
                        + $"{retryAt:HH:mm:ss}Z.";
                    return false;
                }

                _attempts[accountKey] = state with
                {
                    Attempts = state.Attempts + 1,
                    LastAttemptUtc = nowUtc
                };
                blockedReason = "";
                return true;
            }

            _attempts[accountKey] = new AccountRepairState(1, nowUtc, nowUtc);
            blockedReason = "";
            return true;
        }
    }

    /// <summary>
    /// Clears an account's incident once its client has demonstrably recovered, so an unrelated
    /// corruption weeks later gets a full budget rather than inheriting a spent one.
    /// </summary>
    public void RecordRecovered(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            _attempts.Remove(accountKey);
        }
    }

    public int AttemptsFor(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _attempts.TryGetValue(accountKey, out var state) ? state.Attempts : 0;
        }
    }

    private static string RequireKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty account key is required.", nameof(value));
        }

        return value.Trim();
    }

    private sealed record AccountRepairState(
        int Attempts,
        DateTimeOffset FirstAttemptUtc,
        DateTimeOffset LastAttemptUtc);
}
