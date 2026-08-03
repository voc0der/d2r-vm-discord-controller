using System.Text.Json;
using AgentCommon;

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
    /// True after the agent has applied a donor copy but before a healthy rendered frame proves
    /// the client restarted. Unlike host memory, this survives a master restart/lost command ack.
    /// </summary>
    public static bool NeedsReadyAfterRepair(string? statusJson)
    {
        return TryReadRepairFlag(statusJson, "needsReadyAfterSettingsRepair");
    }

    /// <summary>
    /// Returns an opaque identity for the gamma evidence in a status payload. Timestamps here are
    /// deliberately strings, not ordered values: VM, worker, and master clocks can differ. The
    /// tracker only needs to know whether a post-copy failure repeats the exact pre-copy sighting
    /// or carries genuinely changed evidence.
    /// </summary>
    public static string? RepairEvidenceToken(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (document.RootElement.TryGetProperty("d2rSettingsRepair", out var repair)
                && repair.ValueKind == JsonValueKind.Object
                && repair.TryGetProperty("firstSeenUtc", out var firstSeen)
                && firstSeen.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(firstSeen.GetString()))
            {
                // firstSeenUtc is stable for the confirmed incident even while lastSeenUtc moves
                // on every heartbeat/visual observation. It is identity only, never ordered.
                return $"firstSeen:{firstSeen.GetString()}";
            }

            if (repair.ValueKind == JsonValueKind.Object)
            {
                // Compatibility fallback for the earliest agents that emitted the repair object
                // without firstSeenUtc. Equality is still safe; no cross-machine ordering occurs.
                return $"repair:{repair.GetRawText()}";
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads the agent's crash-safe unresolved-incident copy budget. The timestamps originate on
    /// the VM, so callers must treat them relative to the status payload's own <c>timeUtc</c>, never
    /// compare their absolute values with the master's clock.
    /// </summary>
    public static SettingsRepairIncidentEvidence RepairIncidentEvidence(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("d2rSettingsRepair", out var repair)
                || repair.ValueKind != JsonValueKind.Object
                || !repair.TryGetProperty("incidentRepairAttempts", out var attemptsProperty)
                || attemptsProperty.ValueKind != JsonValueKind.Number
                || !attemptsProperty.TryGetInt32(out var attempts)
                || attempts < 1)
            {
                return default;
            }

            return new SettingsRepairIncidentEvidence(
                attempts,
                ReadDateTimeOffset(repair, "incidentFirstRepairUtc"),
                ReadDateTimeOffset(repair, "incidentLastRepairUtc"),
                ReadDateTimeOffset(root, "timeUtc"));
        }
        catch (JsonException)
        {
            return default;
        }
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
            // A reconnect deliberately retains the previous status for diagnostics while clearing
            // StatusReceivedAt. Never let that cached frame authorize copying a settings file:
            // the candidate must have sent status on the connection that is live right now.
            .Where(candidate => candidate.HasStatusFromCurrentConnection)
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

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTimeOffset(out var value)
                ? value
                : null;
    }
}

internal readonly record struct SettingsRepairIncidentEvidence(
    int Attempts,
    DateTimeOffset? FirstRepairUtc,
    DateTimeOffset? LastRepairUtc,
    DateTimeOffset? AgentStatusUtc)
{
    // AgentStatusUtc advances on every heartbeat. The incident identity deliberately does not:
    // rebasing the same first/last attempt again from a later host observation would slide its
    // cooldown forward forever.
    public SettingsRepairIncidentIdentity Identity => new(
        Attempts,
        FirstRepairUtc,
        LastRepairUtc);
}

internal readonly record struct SettingsRepairIncidentIdentity(
    int Attempts,
    DateTimeOffset? FirstRepairUtc,
    DateTimeOffset? LastRepairUtc);

internal readonly record struct SettingsDonorCandidate(
    string AccountKey,
    bool Connected,
    string? StatusJson,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? StatusReceivedAt)
{
    public bool HasStatusFromCurrentConnection =>
        Connected && ConnectedAt is not null && StatusReceivedAt is not null;
}

internal enum SettingsRecoveryStage
{
    None,
    RepairRequired,
    ReadyRequired
}

internal readonly record struct SettingsRecoverySnapshot(
    SettingsRecoveryStage Stage,
    string? DonorAccountKey,
    DateTimeOffset? LastTransitionUtc);

/// <summary>
/// Rate-limits settings repairs per account. A repair closes a live client and overwrites a file,
/// so a client that keeps landing back on the gamma screen must not turn into a repair loop - after
/// <see cref="MaxAttemptsPerIncident"/> tries the account is left alone until the cooldown lapses,
/// and the ordinary warmup escalation takes over.
/// </summary>
internal sealed class SettingsRepairTracker
{
    internal const int MaxAttemptsPerIncident = 2;
    internal static readonly TimeSpan IncidentWindow = D2RSettingsRepairState.IncidentWindow;

    private readonly object _sync = new();
    private readonly Dictionary<string, AccountRepairState> _accounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers a confirmed reset even after the repair command closes D2R. Once the process is
    /// stopped, screenshots can no longer repeat the gamma-screen signal, but a failed/cancelled
    /// copy still has to be retried autonomously.
    /// </summary>
    public void RecordRepairNeeded(
        string accountKey,
        string? evidenceToken,
        DateTimeOffset observedUtc)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            var state = GetOrCreate(accountKey);
            NormalizeExpiredIncident(state, observedUtc);
            if (evidenceToken is not null
                && string.Equals(
                    evidenceToken,
                    state.RecoveredEvidenceToken,
                    StringComparison.Ordinal))
            {
                // AgentRegistry retains the last heartbeat until another status arrives. A ready
                // command can prove recovery first; that exact cached pre-copy Gamma frame must
                // not immediately reopen and recharge the incident.
                return;
            }

            if (evidenceToken is not null)
            {
                state.RecoveredEvidenceToken = null;
            }

            // A fleet heartbeat captured before a successful replacement can still arrive after
            // the command result. Do not turn identical evidence into a second copy. Timestamp
            // ordering is intentionally absent here because the evidence can originate on a VM.
            if (state.Stage == SettingsRecoveryStage.ReadyRequired
                && (evidenceToken is null
                    || string.Equals(
                        evidenceToken,
                        state.LastRepairEvidenceToken,
                        StringComparison.Ordinal)))
            {
                return;
            }

            state.Stage = SettingsRecoveryStage.RepairRequired;
            state.DonorAccountKey = null;
            state.LastTransitionUtc = observedUtc;
            state.LastRepairEvidenceToken = evidenceToken;
        }
    }

    /// <summary>
    /// Applies a fresh status frame to the durable recovery state. A confirmed gamma frame starts
    /// or resumes repair. Other states deliberately leave pending work intact: in particular,
    /// NotRunning follows a failed post-quit copy, and a delayed pre-repair healthy heartbeat must
    /// not clear the incident. Callers record recovery only after an actual ready/warmup success.
    /// </summary>
    public void ObserveCurrentStatus(string accountKey, string? statusJson, DateTimeOffset observedUtc)
    {
        var evidenceToken = SettingsRepairPolicy.RepairEvidenceToken(statusJson);
        if (IsRecoveredEvidence(accountKey, evidenceToken))
        {
            return;
        }

        if (SettingsRepairPolicy.NeedsDonorSettings(statusJson))
        {
            RehydrateAttemptBudget(
                accountKey,
                SettingsRepairPolicy.RepairIncidentEvidence(statusJson),
                observedUtc);
            RecordRepairNeeded(
                accountKey,
                evidenceToken,
                observedUtc);
            return;
        }

        if (SettingsRepairPolicy.NeedsReadyAfterRepair(statusJson))
        {
            var evidence = SettingsRepairPolicy.RepairIncidentEvidence(statusJson);
            RehydrateAttemptBudget(
                accountKey,
                evidence,
                observedUtc);
            RecordReadyNeeded(
                accountKey,
                observedUtc,
                evidenceToken,
                evidence.Attempts > 0 ? evidence.Attempts : null);
        }
    }

    private bool IsRecoveredEvidence(string accountKey, string? evidenceToken)
    {
        if (evidenceToken is null)
        {
            return false;
        }

        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _accounts.TryGetValue(accountKey, out var state)
                && string.Equals(
                    evidenceToken,
                    state.RecoveredEvidenceToken,
                    StringComparison.Ordinal);
        }
    }

    private void RehydrateAttemptBudget(
        string accountKey,
        SettingsRepairIncidentEvidence evidence,
        DateTimeOffset observedUtc)
    {
        if (evidence.Attempts < 1)
        {
            return;
        }

        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            var state = GetOrCreate(accountKey);
            var importedIdentity = evidence.Identity;
            if (state.LastImportedIncident == importedIdentity)
            {
                return;
            }

            NormalizeExpiredIncident(state, observedUtc);

            var importedLast = RebaseAgentEventUtc(
                evidence.LastRepairUtc,
                evidence.AgentStatusUtc,
                observedUtc);
            // Remember every well-shaped identity we considered, including stale/lower evidence.
            // Otherwise it can become authoritative later merely because the host's own budget
            // expired and rebasing anchored the unchanged payload to a newer observation time.
            state.LastImportedIncident = importedIdentity;
            if (observedUtc - importedLast >= IncidentWindow)
            {
                // The sidecar intentionally remains until a healthy frame, but an unresolved
                // incident still receives a fresh copy budget after the documented cooldown.
                return;
            }

            if (evidence.Attempts < state.Attempts
                || (evidence.Attempts == state.Attempts && state.LastAttemptUtc is not null))
            {
                return;
            }

            var importedFirst = RebaseAgentEventUtc(
                evidence.FirstRepairUtc,
                evidence.AgentStatusUtc,
                observedUtc);
            if (importedFirst > importedLast)
            {
                importedFirst = importedLast;
            }

            state.Attempts = Math.Max(state.Attempts, evidence.Attempts);
            state.FirstAttemptUtc = state.FirstAttemptUtc is { } existingFirst
                && existingFirst < importedFirst
                    ? existingFirst
                    : importedFirst;
            state.LastAttemptUtc = importedLast;
        }
    }

    private static DateTimeOffset RebaseAgentEventUtc(
        DateTimeOffset? agentEventUtc,
        DateTimeOffset? agentStatusUtc,
        DateTimeOffset hostObservedUtc)
    {
        if (agentEventUtc is not { } eventUtc || agentStatusUtc is not { } statusUtc)
        {
            // Older/intermediate agents may have the count without both timestamps. Blocking for
            // one full window is safer than granting destructive copies after every master restart.
            return hostObservedUtc;
        }

        var age = statusUtc - eventUtc;
        if (age <= TimeSpan.Zero)
        {
            return hostObservedUtc;
        }

        if (age >= IncidentWindow)
        {
            return hostObservedUtc - IncidentWindow;
        }

        return hostObservedUtc - age;
    }

    public void RecordReadyNeeded(
        string accountKey,
        DateTimeOffset observedUtc,
        string? evidenceToken = null,
        int? durableAttemptCount = null)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            var state = GetOrCreate(accountKey);
            NormalizeExpiredIncident(state, observedUtc);
            if (evidenceToken is not null
                && string.Equals(
                    evidenceToken,
                    state.RecoveredEvidenceToken,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (state.Stage == SettingsRecoveryStage.RepairRequired
                && state.Attempts > 0
                && durableAttemptCount.GetValueOrDefault() < state.Attempts)
            {
                // A Ready frame from before the current charged command can arrive after its
                // failure/result. Current agents carry the sidecar attempt count, so only Ready
                // evidence at least as new as the host's confirmed charge may close this stage.
                return;
            }

            if (evidenceToken is not null)
            {
                state.RecoveredEvidenceToken = null;
                state.LastRepairEvidenceToken = evidenceToken;
            }

            state.Stage = SettingsRecoveryStage.ReadyRequired;
            state.LastTransitionUtc = observedUtc;
            // If a donor export lease was still preparing an old copy, this agent-side latch is
            // stronger evidence: the copy already landed (possibly after a lost acknowledgement).
            if (!state.ActiveLeaseAttemptRecorded)
            {
                state.ActiveLeaseRevoked = true;
            }
        }
    }

    public SettingsRecoverySnapshot RecoveryFor(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _accounts.TryGetValue(accountKey, out var state)
                ? new SettingsRecoverySnapshot(state.Stage, state.DonorAccountKey, state.LastTransitionUtc)
                : new SettingsRecoverySnapshot(SettingsRecoveryStage.None, null, null);
        }
    }

    /// <summary>
    /// Acquires the one destructive-repair lease for an account. Acquisition itself is free: the
    /// caller charges the incident only after the target confirms that its durable Prepared
    /// journal was written, after a donor has actually exported a usable payload.
    /// </summary>
    public SettingsRepairLease? TryAcquireRepair(
        string accountKey,
        DateTimeOffset nowUtc,
        out string blockedReason)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            var state = GetOrCreate(accountKey);
            NormalizeExpiredIncident(state, nowUtc);

            if (state.Stage == SettingsRecoveryStage.None)
            {
                blockedReason = "no confirmed settings-reset incident is pending.";
                return null;
            }

            if (state.ActiveLeaseId is not null)
            {
                blockedReason = "a settings repair for this account is already in progress.";
                return null;
            }

            if (state.Stage == SettingsRecoveryStage.ReadyRequired)
            {
                blockedReason = "the settings replacement already succeeded; only the ready/relaunch phase is pending.";
                return null;
            }

            if (state.Attempts >= MaxAttemptsPerIncident && state.LastAttemptUtc is { } lastAttemptUtc)
            {
                var retryAt = lastAttemptUtc + IncidentWindow;
                blockedReason = $"already replaced this account's settings {state.Attempts} time(s) since "
                    + $"{state.FirstAttemptUtc:HH:mm:ss}Z and it came back corrupt; not trying again before "
                    + $"{retryAt:HH:mm:ss}Z.";
                return null;
            }

            var leaseId = Guid.NewGuid();
            state.ActiveLeaseId = leaseId;
            state.ActiveLeaseRevoked = false;
            state.ActiveLeaseAttemptRecorded = false;
            if (state.Stage == SettingsRecoveryStage.None)
            {
                state.Stage = SettingsRecoveryStage.RepairRequired;
                state.LastTransitionUtc = nowUtc;
            }

            blockedReason = "";
            return new SettingsRepairLease(this, accountKey, leaseId);
        }
    }

    internal void RecordAttempt(string accountKey, Guid leaseId, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var state = RequireLease(accountKey, leaseId);
            if (state.ActiveLeaseRevoked || state.Stage != SettingsRecoveryStage.RepairRequired)
            {
                throw new InvalidOperationException(
                    "The target demonstrated recovery while its donor was being prepared; the settings copy was cancelled.");
            }

            NormalizeExpiredIncident(state, nowUtc);
            if (state.Attempts >= MaxAttemptsPerIncident)
            {
                throw new InvalidOperationException("The settings-repair attempt budget was exhausted while the lease was active.");
            }

            state.Attempts++;
            state.FirstAttemptUtc ??= nowUtc;
            state.LastAttemptUtc = nowUtc;
            state.ActiveLeaseAttemptRecorded = true;
        }
    }

    /// <summary>
    /// Records an attempt only after the target agent confirms that its durable Prepared journal
    /// was written. Unlike <see cref="RecordAttempt"/>, this must tolerate a Ready heartbeat
    /// racing ahead of the command result: that heartbeat is evidence that the same dispatched
    /// copy landed, not a reason to discard its attempt charge.
    /// </summary>
    internal void RecordAgentChargedAttempt(
        string accountKey,
        Guid leaseId,
        DateTimeOffset nowUtc,
        int? durableAttemptCount)
    {
        lock (_sync)
        {
            var state = RequireLease(accountKey, leaseId);
            NormalizeExpiredIncident(state, nowUtc);

            var reportedAttempts = durableAttemptCount is > 0
                ? Math.Min(durableAttemptCount.Value, MaxAttemptsPerIncident)
                : 0;
            if (reportedAttempts > state.Attempts)
            {
                state.Attempts = reportedAttempts;
            }
            else if (reportedAttempts == 0
                && !state.ActiveLeaseAttemptRecorded)
            {
                state.Attempts = Math.Min(state.Attempts + 1, MaxAttemptsPerIncident);
            }

            if (state.Attempts > 0)
            {
                state.FirstAttemptUtc ??= nowUtc;
                state.LastAttemptUtc = nowUtc;
            }

            // This result belongs to the currently leased command and is newer than any status
            // heartbeat that raced it. A charged failure must remain repair-pending even if a
            // delayed pre-command Ready heartbeat arrived first; a charged success immediately
            // advances through RecordRepairApplied below.
            state.Stage = SettingsRecoveryStage.RepairRequired;
            state.DonorAccountKey = null;
            state.LastTransitionUtc = nowUtc;

            state.ActiveLeaseRevoked = false;
            state.ActiveLeaseAttemptRecorded = true;
            state.RecoveredEvidenceToken = null;
        }
    }

    internal void RecordRepairApplied(
        string accountKey,
        Guid leaseId,
        string donorAccountKey,
        DateTimeOffset appliedUtc)
    {
        lock (_sync)
        {
            var state = RequireLease(accountKey, leaseId);
            state.Stage = SettingsRecoveryStage.ReadyRequired;
            state.DonorAccountKey = RequireKey(donorAccountKey);
            state.LastTransitionUtc = appliedUtc;
        }
    }

    internal void ReleaseLease(string accountKey, Guid leaseId)
    {
        lock (_sync)
        {
            if (_accounts.TryGetValue(accountKey, out var state)
                && state.ActiveLeaseId == leaseId)
            {
                state.ActiveLeaseId = null;
                state.ActiveLeaseRevoked = false;
                state.ActiveLeaseAttemptRecorded = false;
                if (state.Stage == SettingsRecoveryStage.None
                    && state.Attempts == 0
                    && state.RecoveredEvidenceToken is null)
                {
                    _accounts.Remove(accountKey);
                }
            }
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
            if (!_accounts.TryGetValue(accountKey, out var state))
            {
                return;
            }

            // Before a copy is charged/dispatched, recovery revokes its active donor-export lease.
            // Keep the identity until Dispose so finally remains safe, but RecordAttempt will now
            // refuse the copy. Once an attempt is already dispatched its result has to become
            // authoritative; revoking at that point cannot recall the command.
            if (state.ActiveLeaseId is not null && state.ActiveLeaseAttemptRecorded)
            {
                return;
            }

            state.Attempts = 0;
            state.FirstAttemptUtc = null;
            state.LastAttemptUtc = null;
            state.Stage = SettingsRecoveryStage.None;
            state.DonorAccountKey = null;
            state.LastTransitionUtc = null;
            state.RecoveredEvidenceToken = state.LastRepairEvidenceToken;
            state.ActiveLeaseRevoked = state.ActiveLeaseId is not null;
            if (state.ActiveLeaseId is null && state.RecoveredEvidenceToken is null)
            {
                _accounts.Remove(accountKey);
            }
        }
    }

    public int AttemptsFor(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _accounts.TryGetValue(accountKey, out var state) ? state.Attempts : 0;
        }
    }

    public bool IsRepairInFlight(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _accounts.TryGetValue(accountKey, out var state) && state.ActiveLeaseId is not null;
        }
    }

    private AccountRepairState GetOrCreate(string accountKey)
    {
        if (!_accounts.TryGetValue(accountKey, out var state))
        {
            state = new AccountRepairState();
            _accounts[accountKey] = state;
        }

        return state;
    }

    private AccountRepairState RequireLease(string accountKey, Guid leaseId)
    {
        if (!_accounts.TryGetValue(accountKey, out var state) || state.ActiveLeaseId != leaseId)
        {
            throw new InvalidOperationException("The settings-repair lease is no longer active.");
        }

        return state;
    }

    private static void NormalizeExpiredIncident(AccountRepairState state, DateTimeOffset nowUtc)
    {
        if (state.LastAttemptUtc is not { } lastAttemptUtc
            || nowUtc - lastAttemptUtc < IncidentWindow)
        {
            return;
        }

        state.Attempts = 0;
        state.FirstAttemptUtc = null;
        state.LastAttemptUtc = null;
    }

    private static string RequireKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty account key is required.", nameof(value));
        }

        return value.Trim();
    }

    private sealed class AccountRepairState
    {
        public int Attempts { get; set; }
        public DateTimeOffset? FirstAttemptUtc { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public Guid? ActiveLeaseId { get; set; }
        public bool ActiveLeaseRevoked { get; set; }
        public bool ActiveLeaseAttemptRecorded { get; set; }
        public SettingsRecoveryStage Stage { get; set; }
        public string? DonorAccountKey { get; set; }
        public DateTimeOffset? LastTransitionUtc { get; set; }
        public string? LastRepairEvidenceToken { get; set; }
        public string? RecoveredEvidenceToken { get; set; }
        public SettingsRepairIncidentIdentity? LastImportedIncident { get; set; }
    }
}

/// <summary>
/// The single-flight token for one account's donor export/copy sequence. Disposing it releases
/// the account even when export, copy, or cancellation throws.
/// </summary>
internal sealed class SettingsRepairLease : IDisposable
{
    private SettingsRepairTracker? _owner;
    private readonly string _accountKey;
    private readonly Guid _leaseId;
    private bool _attemptRecorded;

    internal SettingsRepairLease(SettingsRepairTracker owner, string accountKey, Guid leaseId)
    {
        _owner = owner;
        _accountKey = accountKey;
        _leaseId = leaseId;
    }

    public void RecordAttempt(DateTimeOffset nowUtc)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(SettingsRepairLease));
        if (_attemptRecorded)
        {
            throw new InvalidOperationException("This settings-repair lease has already charged an attempt.");
        }

        owner.RecordAttempt(_accountKey, _leaseId, nowUtc);
        _attemptRecorded = true;
    }

    public void RecordAgentChargedAttempt(DateTimeOffset nowUtc, int? durableAttemptCount)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(SettingsRepairLease));
        if (_attemptRecorded)
        {
            throw new InvalidOperationException("This settings-repair lease has already charged an attempt.");
        }

        owner.RecordAgentChargedAttempt(
            _accountKey,
            _leaseId,
            nowUtc,
            durableAttemptCount);
        _attemptRecorded = true;
    }

    public void RecordRepairApplied(string donorAccountKey, DateTimeOffset appliedUtc)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(SettingsRepairLease));
        if (!_attemptRecorded)
        {
            throw new InvalidOperationException("A settings repair cannot complete before its copy attempt is recorded.");
        }

        owner.RecordRepairApplied(_accountKey, _leaseId, donorAccountKey, appliedUtc);
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReleaseLease(_accountKey, _leaseId);
    }
}
