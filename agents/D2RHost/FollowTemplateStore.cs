using System.Text.Json;
using AgentCommon;

namespace D2RHost;

/// <summary>
/// Owns the fleet's authoritative follow bind and keeps every online VM agent's replica in sync
/// with it.
/// </summary>
/// <remarks>
/// Distribution used to happen exactly once, inside the bind command, to whichever agents were
/// online at that moment. Anything that missed that instant - a VM added to the fleet afterwards,
/// one that was offline or rebuilt, a second hypervisor's clients - stayed blind forever, and
/// follow-auto skipped it silently because an unbound account only reports itself when no account
/// is bound. The host now records what it distributed, every agent advertises a digest of what it
/// actually holds on each status frame, and this reconciles the difference.
///
/// Reconciliation is digest-driven rather than event-driven on purpose: worker-owned VM agents
/// never raise a connect event on the master (their connections belong to their own D2RHost and
/// only surface later in the worker's inventory heartbeat), so diffing the fleet snapshot is the
/// one mechanism that covers local and remote agents identically.
/// </remarks>
public sealed class FollowTemplateStore : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Long enough that a push is never re-issued because the next heartbeat was still in flight
    // with the pre-push digest, short enough that a replica which genuinely lost its file after a
    // successful push is repaired on the following sweep rather than after a bind.
    private static readonly TimeSpan PushGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(15);

    private readonly AppDb _db;
    private readonly FleetRegistry _registry;
    private readonly ILogger<FollowTemplateStore> _logger;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Dictionary<string, PushRecord> _lastPushed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();

    private FollowTemplateState _state;
    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;
    private bool _loggedUnrecorded;

    public FollowTemplateStore(AppDb db, FleetRegistry registry, ILogger<FollowTemplateStore> logger)
    {
        _db = db;
        _registry = registry;
        _logger = logger;
        _state = db.GetFollowTemplates();
    }

    public FollowTemplateState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? BoundAccountKey => State.BoundAccountKey;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sweepCts = new CancellationTokenSource();
        _sweepTask = Task.Run(() => RunSweepLoopAsync(_sweepCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sweepCts is null)
        {
            return;
        }

        await _sweepCts.CancelAsync();
        if (_sweepTask is not null)
        {
            try
            {
                await _sweepTask.WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Shutdown best effort.
            }
        }
    }

    /// <summary>
    /// Records a freshly captured friend-row bind as the fleet's authoritative one. Deliberately
    /// does not touch the leader rolodex: <c>follow_set_template</c> never has on the agent side
    /// either, so re-binding the friend row keeps whatever in-game nametags are already bound.
    /// </summary>
    public void SetFriendTemplate(string fingerprint, string? boundAccountKey)
    {
        Mutate(state => state with
        {
            FriendFingerprint = fingerprint.Trim(),
            BoundAccountKey = boundAccountKey,
            FriendRecorded = true
        });
    }

    /// <summary>Adds one in-game nametag to the rolodex, preserving bind order.</summary>
    public void AppendLeaderTemplate(string fingerprint)
    {
        Mutate(state => state with
        {
            LeaderFingerprints = PartyNameFingerprintList.Append(state.LeaderFingerprints, fingerprint),
            LeaderRecorded = true
        });
    }

    /// <summary>Drops one nametag - the rollback path for a bind that captured a bot.</summary>
    public void RemoveLeaderTemplate(string fingerprint)
    {
        Mutate(state => state with
        {
            LeaderFingerprints = PartyNameFingerprintList.Remove(state.LeaderFingerprints, fingerprint),
            LeaderRecorded = true
        });
    }

    /// <summary>
    /// Records a full unbind. This is a recorded empty state, not an absent one, so the sweep
    /// actively clears replicas that were offline when the operator unbound. Both halves become
    /// authoritative because the unbind command clears both files on every online agent.
    /// </summary>
    public void Clear()
    {
        Mutate(_ => new FollowTemplateState(null, [], null, FriendRecorded: true, LeaderRecorded: true));
    }

    /// <summary>
    /// One health line describing the fleet's bind and which online VMs are out of step with it.
    /// Read-only - it runs the same push planner the sweep does but sends nothing, so divergence
    /// is visible in /d2r health rather than only inferable from a client that never joins.
    /// </summary>
    public string FormatHealthLine()
    {
        var state = State;
        if (!state.Recorded)
        {
            return "Follow bind: none recorded - run /d2r follow bind:true once to arm template sync";
        }

        if (!state.HasFriendTemplate)
        {
            return "Follow bind: cleared";
        }

        var online = _registry.GetAccountConnectivity().Online;
        var diverged = new List<string>();
        foreach (var entry in online)
        {
            var agent = _registry.GetAgent(entry.Value.AgentId);
            if (agent is null)
            {
                continue;
            }

            var pushes = PlanPushes(
                state,
                ReadAdvertisedDigests(agent.LastStatusJson),
                LastPushedFor(entry.Value.AgentId));
            if (pushes.Count > 0)
            {
                diverged.Add(entry.Key);
            }
        }

        var source = string.IsNullOrWhiteSpace(state.BoundAccountKey)
            ? ""
            : $" from {state.BoundAccountKey}";
        var nametags = state.LeaderFingerprints.Count > 0
            ? $" + {state.LeaderFingerprints.Count} in-game nametag(s)"
            : "";
        var synced = $"{online.Length - diverged.Count}/{online.Length} online VM(s) in sync";
        var pending = diverged.Count > 0
            ? $", syncing {string.Join(", ", diverged)}"
            : "";
        return $"Follow bind: friend row{source}{nametags}, {synced}{pending}";
    }

    /// <summary>
    /// Pushes the authoritative bind to every online VM agent whose advertised digests do not
    /// match it. Safe to call concurrently; overlapping calls are serialized.
    /// </summary>
    public async Task<FollowTemplateSyncResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var state = State;
        if (!state.Recorded)
        {
            // Nothing to reconcile against, and guessing would be worse than waiting: on a host
            // upgraded into this feature the agents may already hold a perfectly good bind that
            // predates the table. One bind command arms sync permanently.
            if (!_loggedUnrecorded)
            {
                _loggedUnrecorded = true;
                _logger.LogInformation(
                    "Follow-template sync is idle: the host has no recorded bind yet. Run /d2r follow bind:true once and every current and future VM agent is kept in sync automatically.");
            }

            return FollowTemplateSyncResult.Idle;
        }

        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            // Re-read inside the lock: a bind that lands while a sweep is waiting must win.
            state = State;
            var repaired = new List<FollowTemplateRepair>();
            var failures = new List<string>();
            var inSync = 0;

            foreach (var entry in _registry.GetAccountConnectivity().Online)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var accountKey = entry.Key;
                var agentId = entry.Value.AgentId;
                var agent = _registry.GetAgent(agentId);
                if (agent is null)
                {
                    continue;
                }

                var advertised = ReadAdvertisedDigests(agent.LastStatusJson);
                var actions = PlanPushes(state, advertised, LastPushedFor(agentId));
                if (actions.Count == 0)
                {
                    inSync++;
                    continue;
                }

                var pushed = await ApplyAsync(accountKey, agentId, actions, failures, cancellationToken);
                if (pushed)
                {
                    RecordPush(agentId, state);
                    repaired.Add(new FollowTemplateRepair(
                        accountKey,
                        string.Join(", ", actions.Select(action => action.Description))));
                }
            }

            if (repaired.Count > 0 || failures.Count > 0)
            {
                _logger.LogInformation(
                    "Follow-template sync repaired {RepairedCount} agent(s) [{Repaired}], {FailureCount} failure(s) [{Failures}], {InSyncCount} already in sync.",
                    repaired.Count,
                    string.Join("; ", repaired.Select(entry => $"{entry.AccountKey} ({entry.Detail})")),
                    failures.Count,
                    string.Join("; ", failures),
                    inSync);
            }

            return new FollowTemplateSyncResult(repaired, failures, inSync);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>
    /// The push plan for one agent. Ordering is deliberate: a friend-template clear also deletes
    /// the agent's leader file (that is <c>follow_clear_template</c>'s documented behaviour, so a
    /// stale nametag mask can never outlive the bind it belonged to), which makes any leader work
    /// alongside it redundant.
    /// </summary>
    internal static IReadOnlyList<FollowTemplatePush> PlanPushes(
        FollowTemplateState state,
        AdvertisedFollowTemplates advertised,
        PushRecord? lastPushed)
    {
        if (!state.Recorded)
        {
            return [];
        }

        // An agent too old to advertise digests cannot be diffed, so fall back to what this host
        // last pushed it. Without that fallback every sweep would re-push it forever.
        // An unrecorded half is never a mismatch - the host has no opinion about it yet.
        var friendMatches = !state.FriendRecorded
            || (advertised.Known
                ? string.Equals(advertised.FriendDigest, state.FriendDigest, StringComparison.Ordinal)
                : string.Equals(lastPushed?.FriendDigest, state.FriendDigest, StringComparison.Ordinal));
        var leaderMatches = !state.LeaderRecorded
            || (advertised.Known
                ? string.Equals(advertised.LeaderDigest, state.LeaderDigest, StringComparison.Ordinal)
                : string.Equals(lastPushed?.LeaderDigest, state.LeaderDigest, StringComparison.Ordinal));

        // A push whose effect has not had time to reach a heartbeat yet is not a divergence.
        if (lastPushed is { } record
            && DateTimeOffset.UtcNow - record.PushedUtc < PushGrace
            && string.Equals(record.FriendDigest, state.FriendDigest, StringComparison.Ordinal)
            && string.Equals(record.LeaderDigest, state.LeaderDigest, StringComparison.Ordinal))
        {
            return [];
        }

        if (friendMatches && leaderMatches)
        {
            return [];
        }

        var pushes = new List<FollowTemplatePush>();
        if (state.FriendRecorded && !state.HasFriendTemplate)
        {
            // One command covers both files here, so a replica that missed an unbind is fully
            // reset whichever half of it diverged.
            pushes.Add(new FollowTemplatePush(
                "follow_clear_template",
                new { },
                "cleared a stale bind"));
            return pushes;
        }

        if (!friendMatches)
        {
            pushes.Add(new FollowTemplatePush(
                "follow_set_template",
                new { fingerprint = state.FriendFingerprint },
                "restored the friend-row bind"));
        }

        if (!leaderMatches)
        {
            pushes.Add(state.LeaderFingerprints.Count == 0
                ? new FollowTemplatePush(
                    "follow_clear_leader_template",
                    new { },
                    "cleared stale in-game nametags")
                : new FollowTemplatePush(
                    "follow_set_leader_template",
                    new
                    {
                        fingerprint = PartyNameFingerprintList.Serialize(state.LeaderFingerprints),
                        append = false
                    },
                    $"restored {state.LeaderFingerprints.Count} in-game nametag(s)"));
        }

        return pushes;
    }

    internal static AdvertisedFollowTemplates ReadAdvertisedDigests(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return AdvertisedFollowTemplates.Unknown;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("followTemplates", out var templates)
                || templates.ValueKind != JsonValueKind.Object)
            {
                return AdvertisedFollowTemplates.Unknown;
            }

            var friendDigest = ReadString(templates, "friendDigest");
            var leaderDigest = ReadString(templates, "leaderDigest");
            if (friendDigest is null || leaderDigest is null)
            {
                return AdvertisedFollowTemplates.Unknown;
            }

            return new AdvertisedFollowTemplates(true, friendDigest, leaderDigest);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return AdvertisedFollowTemplates.Unknown;
        }
    }

    private async Task<bool> ApplyAsync(
        string accountKey,
        string agentId,
        IReadOnlyList<FollowTemplatePush> pushes,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        foreach (var push in pushes)
        {
            try
            {
                var result = await _registry.SendCommandAsync(
                    agentId, push.Command, push.Args, PushTimeout, cancellationToken);
                if (!result.Ok)
                {
                    failures.Add($"{accountKey}: {push.Command} - {result.Message}");
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Follow-template {Command} failed for {AccountKey}.", push.Command, accountKey);
                failures.Add($"{accountKey}: {push.Command} - {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private async Task RunSweepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, cancellationToken);
                await ReconcileAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep must never take the host down, and the next one is a minute away.
                _logger.LogWarning(ex, "Follow-template sync sweep failed.");
            }
        }
    }

    private void Mutate(Func<FollowTemplateState, FollowTemplateState> update)
    {
        FollowTemplateState updated;
        lock (_stateLock)
        {
            updated = update(_state);
            _state = updated;
        }

        _db.SaveFollowTemplates(updated);
        // Every agent's replica is now suspect against the new authority; drop the push memory so
        // an agent too old to advertise digests is brought forward as well.
        lock (_lastPushed)
        {
            _lastPushed.Clear();
        }
    }

    private PushRecord? LastPushedFor(string agentId)
    {
        lock (_lastPushed)
        {
            return _lastPushed.TryGetValue(agentId, out var record) ? record : null;
        }
    }

    private void RecordPush(string agentId, FollowTemplateState state)
    {
        lock (_lastPushed)
        {
            _lastPushed[agentId] = new PushRecord(
                state.FriendDigest,
                state.LeaderDigest,
                DateTimeOffset.UtcNow);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    internal sealed record PushRecord(string FriendDigest, string LeaderDigest, DateTimeOffset PushedUtc);
}

internal sealed record FollowTemplatePush(string Command, object Args, string Description);

/// <summary>
/// What a VM agent says it currently holds. <see cref="Known"/> is false for an agent whose build
/// predates the digest field, which must be treated as "cannot tell" rather than "holds nothing".
/// </summary>
public sealed record AdvertisedFollowTemplates(bool Known, string FriendDigest, string LeaderDigest)
{
    public static AdvertisedFollowTemplates Unknown { get; } =
        new(false, FollowTemplateDigest.None, FollowTemplateDigest.None);
}

public sealed record FollowTemplateRepair(string AccountKey, string Detail);

public sealed record FollowTemplateSyncResult(
    IReadOnlyList<FollowTemplateRepair> Repaired,
    IReadOnlyList<string> Failures,
    int InSync)
{
    public static FollowTemplateSyncResult Idle { get; } = new([], [], 0);

    public bool DidWork => Repaired.Count > 0 || Failures.Count > 0;

    public string RepairedAccountList =>
        string.Join(", ", Repaired.Select(entry => entry.AccountKey));
}
