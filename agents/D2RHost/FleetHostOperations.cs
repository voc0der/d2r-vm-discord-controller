using AgentCommon;
using System.Net.WebSockets;

namespace D2RHost;

/// <summary>
/// Routes physical-VM operations to the D2RHost that owns the account. The
/// master executes commands for its own node directly and tunnels commands for
/// worker nodes through their authenticated host-agent connection.
/// </summary>
public sealed class FleetHostOperations
{
    private static readonly TimeSpan RemoteCommandHeadroom = TimeSpan.FromSeconds(10);

    private readonly HostConfig _config;
    private readonly AgentRegistry _localRegistry;
    private readonly FleetRegistry _fleetRegistry;
    private readonly HyperVOperations _localHyperV;
    private readonly HostSystemOperations _localSystem;

    public FleetHostOperations(
        HostConfig config,
        AgentRegistry localRegistry,
        FleetRegistry fleetRegistry,
        HyperVOperations localHyperV,
        HostSystemOperations localSystem)
    {
        _config = config;
        _localRegistry = localRegistry;
        _fleetRegistry = fleetRegistry;
        _localHyperV = localHyperV;
        _localSystem = localSystem;
    }

    public async Task<CommandResult> HandleCommandAsync(
        AccountConfig account,
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var nodeId = ResolveNodeId(account);
        if (IsLocalNode(nodeId))
        {
            return await _localHyperV.HandleCommandAsync(request, cancellationToken);
        }

        var node = _localRegistry.GetAgent(nodeId);
        if (node?.Connected != true)
        {
            return CommandResult.Failure($"D2RHost worker \"{nodeId}\" is offline; VM command skipped.");
        }

        try
        {
            // The worker owns PowerShell execution policy. Its heartbeat advertises
            // that non-secret budget; older workers fall back to the 15-minute worker
            // safety ceiling instead of inheriting the master's unrelated timeout.
            var timeout = TimeSpan.FromSeconds(
                    _fleetRegistry.GetNodeVmCommandTimeoutSeconds(nodeId))
                + RemoteCommandHeadroom;
            var result = await _localRegistry.SendCommandAsync(
                nodeId,
                request.Command,
                request.Args,
                timeout,
                cancellationToken);

            return result.Ok
                ? CommandResult.Success(result.Message, result.Data)
                : CommandResult.Failure(result.Message, result.Data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebSocketException)
        {
            return CommandResult.Failure(
                $"D2RHost worker \"{nodeId}\" did not confirm the VM command; its outcome may be unknown: {ex.Message}");
        }
    }

    public string ResolveNodeId(AccountConfig account)
    {
        return string.IsNullOrWhiteSpace(account.NodeId)
            ? _config.NodeId
            : account.NodeId;
    }

    public bool IsLocalNode(string nodeId)
    {
        return string.Equals(nodeId, _config.NodeId, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> NodeIds(bool onlineOnly)
    {
        var nodes = new List<string> { _config.NodeId };
        nodes.AddRange(_config.Agents
            .Where(pair => string.Equals(pair.Value.Kind, "host", StringComparison.OrdinalIgnoreCase))
            .Where(pair => !onlineOnly || _localRegistry.GetAgent(pair.Key)?.Connected == true)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        return nodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool IsKnownNode(string nodeId)
    {
        return IsLocalNode(nodeId)
            || (_config.Agents.TryGetValue(nodeId, out var configured)
                && string.Equals(configured.Kind, "host", StringComparison.OrdinalIgnoreCase));
    }

    public bool IsNodeOnline(string nodeId)
    {
        return IsLocalNode(nodeId)
            || (IsKnownNode(nodeId) && _localRegistry.GetAgent(nodeId)?.Connected == true);
    }

    public async Task<CommandResult> QueueSystemActionAsync(
        string nodeId,
        HostSystemPowerAction action,
        CancellationToken cancellationToken = default)
    {
        if (IsLocalNode(nodeId))
        {
            if (!OperatingSystem.IsWindows())
            {
                return CommandResult.Failure($"System power actions require Windows on node \"{nodeId}\".");
            }

            _localSystem.Queue(action);
            return CommandResult.Success($"{nodeId}: {HostSystemPowerActions.FormatQueuedMessage(action)}");
        }

        if (!IsKnownNode(nodeId))
        {
            return CommandResult.Failure($"Unknown D2RHost node \"{nodeId}\".");
        }

        if (_localRegistry.GetAgent(nodeId)?.Connected != true)
        {
            return CommandResult.Failure($"D2RHost worker \"{nodeId}\" is offline; system action skipped.");
        }

        var command = action switch
        {
            HostSystemPowerAction.Sleep => "system_sleep",
            HostSystemPowerAction.Shutdown => "system_shutdown",
            HostSystemPowerAction.Restart => "system_restart",
            _ => throw new InvalidOperationException($"Unsupported system action: {action}")
        };
        try
        {
            var result = await _localRegistry.SendCommandAsync(
                nodeId,
                command,
                new { requestedBy = _config.NodeId },
                TimeSpan.FromSeconds(20),
                cancellationToken);
            return result.Ok
                ? CommandResult.Success($"{nodeId}: {result.Message}", result.Data)
                : CommandResult.Failure($"{nodeId}: {result.Message}", result.Data);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or WebSocketException)
        {
            return CommandResult.Failure(
                $"D2RHost worker \"{nodeId}\" did not confirm the system action; it may already be queued: {ex.Message}");
        }
    }
}
