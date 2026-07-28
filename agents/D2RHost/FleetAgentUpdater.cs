using AgentCommon;

namespace D2RHost;

/// <summary>
/// Drives satellite auto-update for the whole fleet from the master, including VM agents owned by
/// worker nodes.
/// </summary>
/// <remarks>
/// Auto-update used to be purely local: each D2RHost offered <c>self_update</c> to an agent as it
/// authenticated, in AgentRegistry. On the master that works, but it leaves worker-owned agents
/// depending entirely on their own worker, and two things go wrong there.
///
/// First, a worker decides once at process start whether satellite auto-update is allowed at all -
/// it is gated on that worker's own update check succeeding. One failed check (a network blip, a
/// rate-limited release lookup) disables updates for every agent on that node for the entire life
/// of the process, and says so only at Debug level, so the node quietly stops updating and nothing
/// surfaces it. Second, the master never offered <c>self_update</c> to the worker D2RHost itself,
/// so a worker only ever updated if someone restarted it by hand - which is precisely how a node
/// ends up stuck running a build old enough for its local hook to matter.
///
/// The master now sweeps the combined fleet instead. Like the follow-template sync, this is
/// snapshot-diffing rather than event-driven, because worker-owned agents never raise a connect
/// event on the master - they only appear in a worker's inventory heartbeat. Commands route
/// through FleetRegistry, which wraps them in <c>agent_command</c> for remote nodes, so local and
/// worker-owned agents are handled identically.
/// </remarks>
public sealed class FleetAgentUpdater : IHostedService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstSweepDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCommandTimeout = TimeSpan.FromSeconds(45);

    private readonly HostConfig _config;
    private readonly AgentAutoUpdateState _autoUpdate;
    private readonly FleetRegistry _fleet;
    private readonly AgentRegistry _localRegistry;
    private readonly DiscordNotificationQueue _notifications;
    private readonly SatelliteUpdateGate _updateGate;
    private readonly ILogger<FleetAgentUpdater> _logger;
    private readonly SemaphoreSlim _sweepLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;

    public FleetAgentUpdater(
        HostConfig config,
        AgentAutoUpdateState autoUpdate,
        FleetRegistry fleet,
        AgentRegistry localRegistry,
        DiscordNotificationQueue notifications,
        SatelliteUpdateGate updateGate,
        ILogger<FleetAgentUpdater> logger)
    {
        _config = config;
        _autoUpdate = autoUpdate;
        _fleet = fleet;
        _localRegistry = localRegistry;
        _notifications = notifications;
        _updateGate = updateGate;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_autoUpdate.Enabled)
        {
            // Deliberately a warning, not a debug line. This is the state that silently stops a
            // whole node updating, and the only way anyone found out before was by noticing a
            // version drift weeks later.
            _logger.LogWarning(
                "Fleet agent auto-update is disabled because this host's own update check did not complete: {Reason}",
                _autoUpdate.Reason);
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _sweepTask = Task.Run(() => RunSweepLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
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
    /// Offers <c>self_update</c> to every connected node and VM agent in the fleet that has not
    /// already been offered one at its currently reported version.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        await _sweepLock.WaitAsync(cancellationToken);
        try
        {
            var offered = 0;

            // Workers first. A worker that is behind cannot relay commands it does not know
            // about, so bringing the node up to date is what makes everything below it reachable.
            foreach (var node in _localRegistry.Snapshot()
                         .Where(agent => agent.Connected
                             && string.Equals(agent.Kind, "host", StringComparison.OrdinalIgnoreCase)))
            {
                if (await TryOfferAsync(node.Id, node.Version, isNode: true, cancellationToken))
                {
                    offered++;
                }
            }

            foreach (var agent in _fleet.Snapshot().Where(agent => agent.Connected))
            {
                if (await TryOfferAsync(agent.Id, agent.Version, isNode: false, cancellationToken))
                {
                    offered++;
                }
            }

            return offered;
        }
        finally
        {
            _sweepLock.Release();
        }
    }

    private async Task<bool> TryOfferAsync(
        string agentId,
        string? version,
        bool isNode,
        CancellationToken cancellationToken)
    {
        // Shared with the authentication hook, and keyed by reported version so a satellite that
        // comes back on an older build is offered an update again while one that is already
        // current is not asked every sweep.
        if (!_updateGate.TryBeginOffer(agentId, version))
        {
            return false;
        }

        var retryable = false;
        try
        {
            var result = isNode
                ? await _localRegistry.SendCommandAsync(
                    agentId, "self_update", BuildArgs(), UpdateCommandTimeout, cancellationToken)
                : await _fleet.SendCommandAsync(
                    agentId, "self_update", BuildArgs(), UpdateCommandTimeout, cancellationToken);

            if (!result.Ok)
            {
                _logger.LogWarning(
                    "Fleet auto-update command failed for {AgentId}: {Message}", agentId, result.Message);
                return false;
            }

            if (TryReadSelfUpdateStarted(result.Data, out var current, out var latest, out var logPath))
            {
                _notifications.Enqueue(
                    SatelliteUpdateNotifications.FormatStarted(
                        agentId,
                        isNode,
                        version,
                        current,
                        latest,
                        logPath));
                _logger.LogInformation(
                    "Fleet auto-update started for {AgentId}: {Current} -> {Latest}", agentId, current, latest);
                return true;
            }

            _logger.LogDebug("Fleet auto-update checked {AgentId}: {Message}", agentId, result.Message);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            retryable = true;
            throw;
        }
        catch (Exception ex)
        {
            // Do not remember a failed offer: the next sweep should try again.
            retryable = true;
            _logger.LogWarning(ex, "Fleet auto-update command failed for {AgentId}.", agentId);
            return false;
        }
        finally
        {
            _updateGate.CompleteOffer(agentId, version, retryable);
        }
    }

    private object BuildArgs()
    {
        return new
        {
            initiatedBy = "fleet",
            nodeId = _config.NodeId,
            hostVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        };
    }

    private async Task RunSweepLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the fleet settle first: worker inventory arrives on a heartbeat, so sweeping
            // immediately at startup would only ever see the master's own agents.
            await Task.Delay(FirstSweepDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(cancellationToken);
                await Task.Delay(SweepInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fleet agent auto-update sweep failed.");
                try
                {
                    await Task.Delay(SweepInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal static bool TryReadSelfUpdateStarted(
        System.Text.Json.JsonElement? data,
        out string? currentVersion,
        out string? latestVersion,
        out string? logPath)
    {
        currentVersion = null;
        latestVersion = null;
        logPath = null;
        if (data is not { } root
            || root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("updateStarted", out var started)
            || started.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            || !started.GetBoolean())
        {
            return false;
        }

        currentVersion = ReadString(root, "currentVersion");
        latestVersion = ReadString(root, "latestVersion");
        logPath = ReadString(root, "logPath");
        return true;
    }

    private static string? ReadString(System.Text.Json.JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == System.Text.Json.JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
