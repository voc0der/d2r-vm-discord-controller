using System.Text.Json;

namespace D2RHost;

/// <summary>
/// Presents the master's local VM agents and every connected worker's local VM
/// agents as one logical registry. Worker inventory is carried in the status
/// heartbeat of an authenticated kind=host AgentClient connection.
/// </summary>
public sealed class FleetRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HostConfig _config;
    private readonly AgentRegistry _localRegistry;
    private readonly ILogger<FleetRegistry> _logger;

    public FleetRegistry(
        HostConfig config,
        AgentRegistry localRegistry,
        ILogger<FleetRegistry> logger)
    {
        _config = config;
        _localRegistry = localRegistry;
        _logger = logger;
        _localRegistry.ConnectivityChanged += () => ConnectivityChanged?.Invoke();
    }

    public event Action? ConnectivityChanged;

    public IReadOnlyDictionary<string, AccountConfig> Accounts => BuildSnapshot().Accounts;

    public IReadOnlyList<AgentSnapshot> Snapshot()
    {
        return BuildSnapshot().Agents.Values
            .OrderBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public AgentSnapshot? GetAgent(string agentId)
    {
        return BuildSnapshot().Agents.TryGetValue(agentId, out var agent)
            ? agent
            : null;
    }

    public HostAgentConfig? GetAgentConfig(string agentId)
    {
        return BuildSnapshot().AgentConfigs.TryGetValue(agentId, out var config)
            ? config
            : null;
    }

    public IReadOnlyList<FleetNodeSnapshot> NodeSnapshot()
    {
        var fleet = BuildSnapshot();
        var nodes = new List<FleetNodeSnapshot>
        {
            BuildLocalNodeSnapshot(fleet)
        };

        foreach (var (nodeId, configured) in _config.Agents
                     .Where(pair => IsHostAgent(pair.Value))
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var connection = _localRegistry.GetAgent(nodeId);
            var inventory = TryParseWorkerInventory(nodeId, connection?.LastStatusJson);
            var nodeAgents = fleet.AgentNodes
                .Where(pair => string.Equals(pair.Value, nodeId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => fleet.Agents[pair.Key])
                .ToArray();

            nodes.Add(new FleetNodeSnapshot(
                nodeId,
                configured.DisplayName,
                IsLocal: false,
                Connected: connection?.Connected == true,
                connection?.HostName ?? inventory?.HostName,
                connection?.Version,
                connection?.LastSeenAt,
                nodeAgents.Count(agent => agent.Connected),
                nodeAgents.Length));
        }

        return nodes;
    }

    public int GetNodeVmCommandTimeoutSeconds(string nodeId)
    {
        var node = _localRegistry.GetAgent(nodeId);
        if (node?.Connected == true && !HasStatusFromCurrentConnection(node))
        {
            return WorkerNodeOperations.MaximumCommandDurationSeconds;
        }

        var inventory = TryParseWorkerInventory(nodeId, node?.LastStatusJson);
        return Math.Clamp(
            inventory?.VmCommandTimeoutSeconds ?? WorkerNodeOperations.MaximumCommandDurationSeconds,
            10,
            WorkerNodeOperations.MaximumCommandDurationSeconds);
    }

    public async Task<CommandResultInfo> SendCommandAsync(
        string agentId,
        string command,
        object? args = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var fleet = BuildSnapshot();
        if (!fleet.Agents.TryGetValue(agentId, out var agent))
        {
            throw new InvalidOperationException($"Agent \"{agentId}\" is not configured in the fleet.");
        }

        if (!agent.Connected)
        {
            var nodeSuffix = fleet.AgentNodes.TryGetValue(agentId, out var unavailableNode)
                ? $" on D2RHost worker \"{unavailableNode}\""
                : "";
            throw new InvalidOperationException($"Agent \"{agentId}\"{nodeSuffix} is offline.");
        }

        if (!fleet.AgentNodes.TryGetValue(agentId, out var nodeId)
            || IsLocalNode(nodeId))
        {
            return await _localRegistry.SendCommandAsync(agentId, command, args, timeout, cancellationToken);
        }

        var node = _localRegistry.GetAgent(nodeId);
        if (node?.Connected != true)
        {
            throw new InvalidOperationException($"D2RHost worker \"{nodeId}\" is offline.");
        }

        var nestedTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var nestedTimeoutMs = (int)Math.Clamp(nestedTimeout.TotalMilliseconds, 1, int.MaxValue);
        var outerTimeout = nestedTimeout + TimeSpan.FromSeconds(10);
        var result = await _localRegistry.SendCommandAsync(
            nodeId,
            "agent_command",
            new
            {
                agentId,
                command,
                args = args ?? new { },
                timeoutMs = nestedTimeoutMs
            },
            outerTimeout,
            cancellationToken);

        return new CommandResultInfo(
            agentId,
            result.CommandId,
            result.Ok,
            result.Message,
            result.Data);
    }

    private FleetSnapshot BuildSnapshot()
    {
        var accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase);
        var agents = new Dictionary<string, AgentSnapshot>(StringComparer.OrdinalIgnoreCase);
        var agentConfigs = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase);
        var agentNodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (agentId, configured) in _config.Agents.Where(pair => IsVmAgent(pair.Value)))
        {
            agents[agentId] = _localRegistry.GetAgent(agentId)
                ?? OfflineAgent(agentId, configured.Kind, configured.DisplayName);
            agentConfigs[agentId] = configured;
            agentNodes[agentId] = _config.NodeId;
        }

        foreach (var (accountKey, configured) in _config.Accounts)
        {
            accounts[accountKey] = CloneAccount(configured, _config.NodeId);
        }

        foreach (var (nodeId, _) in _config.Agents
                     .Where(pair => IsHostAgent(pair.Value))
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var node = _localRegistry.GetAgent(nodeId);
            var inventory = TryParseWorkerInventory(nodeId, node?.LastStatusJson);
            if (inventory is null)
            {
                continue;
            }

            var rejectedAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var remoteAgent in inventory.Agents.Where(agent =>
                         string.Equals(agent.Kind, "vm", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(remoteAgent.Id, remoteAgent.Snapshot.Id, StringComparison.OrdinalIgnoreCase))
                {
                    rejectedAgentIds.Add(remoteAgent.Id);
                    _logger.LogWarning(
                        "Ignoring fleet agent {AgentId} from worker {NodeId} because its nested snapshot identifies as {SnapshotAgentId}.",
                        remoteAgent.Id,
                        nodeId,
                        remoteAgent.Snapshot.Id);
                    continue;
                }

                if (string.Equals(remoteAgent.Id, _config.NodeId, StringComparison.OrdinalIgnoreCase)
                    || agents.ContainsKey(remoteAgent.Id)
                    || (_config.Agents.TryGetValue(remoteAgent.Id, out var sameIdConfig)
                        && IsHostAgent(sameIdConfig)))
                {
                    rejectedAgentIds.Add(remoteAgent.Id);
                    _logger.LogWarning(
                        "Ignoring duplicate fleet agent ID {AgentId} advertised by worker {NodeId}; agent IDs must be globally unique.",
                        remoteAgent.Id,
                        nodeId);
                    continue;
                }

                // Snapshot.Connected was evaluated by the worker against its own
                // heartbeat policy and clock. Re-evaluating its LastSeenAt against
                // the master's clock can mark healthy VMs offline under clock skew.
                var connected = node?.Connected == true
                    && HasStatusFromCurrentConnection(node)
                    && remoteAgent.Snapshot.Connected;
                agents[remoteAgent.Id] = remoteAgent.Snapshot with
                {
                    Kind = "vm",
                    DisplayName = remoteAgent.DisplayName ?? remoteAgent.Snapshot.DisplayName,
                    Connected = connected
                };
                agentConfigs[remoteAgent.Id] = new HostAgentConfig
                {
                    Kind = "vm",
                    DisplayName = remoteAgent.DisplayName,
                    RemoteUrl = remoteAgent.RemoteUrl,
                    SharedSecret = ""
                };
                agentNodes[remoteAgent.Id] = nodeId;
            }

            foreach (var remoteAccount in inventory.Accounts)
            {
                if (accounts.ContainsKey(remoteAccount.Key))
                {
                    _logger.LogWarning(
                        "Ignoring duplicate fleet account key {AccountKey} advertised by worker {NodeId}; account keys must be globally unique.",
                        remoteAccount.Key,
                        nodeId);
                    continue;
                }

                if (rejectedAgentIds.Contains(remoteAccount.AgentId)
                    || string.Equals(remoteAccount.AgentId, _config.NodeId, StringComparison.OrdinalIgnoreCase)
                    || (_config.Agents.TryGetValue(remoteAccount.AgentId, out var configuredAgentWithSameId)
                        && IsHostAgent(configuredAgentWithSameId))
                    || (agentNodes.TryGetValue(remoteAccount.AgentId, out var ownerNode)
                        && !string.Equals(ownerNode, nodeId, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(
                        "Ignoring fleet account {AccountKey} from worker {NodeId} because agent ID {AgentId} is owned by another node.",
                        remoteAccount.Key,
                        nodeId,
                        remoteAccount.AgentId);
                    continue;
                }

                accounts[remoteAccount.Key] = new AccountConfig
                {
                    NodeId = nodeId,
                    AgentId = remoteAccount.AgentId,
                    DisplayName = remoteAccount.DisplayName,
                    VmName = remoteAccount.VmName,
                    CharacterSlot = remoteAccount.CharacterSlot
                };

                if (!agents.ContainsKey(remoteAccount.AgentId))
                {
                    agents[remoteAccount.AgentId] = OfflineAgent(
                        remoteAccount.AgentId,
                        "vm",
                        remoteAccount.DisplayName);
                    agentConfigs[remoteAccount.AgentId] = new HostAgentConfig
                    {
                        Kind = "vm",
                        DisplayName = remoteAccount.DisplayName,
                        SharedSecret = ""
                    };
                    agentNodes[remoteAccount.AgentId] = nodeId;
                }
            }
        }

        return new FleetSnapshot(accounts, agents, agentConfigs, agentNodes);
    }

    private FleetNodeSnapshot BuildLocalNodeSnapshot(FleetSnapshot fleet)
    {
        var localAgents = fleet.AgentNodes
            .Where(pair => IsLocalNode(pair.Value))
            .Select(pair => fleet.Agents[pair.Key])
            .ToArray();
        return new FleetNodeSnapshot(
            _config.NodeId,
            Environment.MachineName,
            IsLocal: true,
            Connected: true,
            Environment.MachineName,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            DateTimeOffset.UtcNow,
            localAgents.Count(agent => agent.Connected),
            localAgents.Length);
    }

    private WorkerInventory? TryParseWorkerInventory(string expectedNodeId, string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodeId", out var nodeIdProperty)
                || nodeIdProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nodeIdProperty.GetString()))
            {
                return null;
            }

            var agents = new List<WorkerAgentInventory>();
            if (root.TryGetProperty("agents", out var agentsElement)
                && agentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in agentsElement.EnumerateArray())
                {
                    var id = ReadString(element, "id");
                    var snapshot = element.TryGetProperty("snapshot", out var snapshotElement)
                        ? snapshotElement.Deserialize<AgentSnapshot>(JsonOptions)
                        : null;
                    if (string.IsNullOrWhiteSpace(id) || snapshot is null)
                    {
                        continue;
                    }

                    agents.Add(new WorkerAgentInventory(
                        id,
                        ReadString(element, "kind") ?? "vm",
                        ReadString(element, "displayName"),
                        ReadString(element, "remoteUrl"),
                        snapshot));
                }
            }

            var accounts = new List<WorkerAccountInventory>();
            if (root.TryGetProperty("accounts", out var accountsElement)
                && accountsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in accountsElement.EnumerateArray())
                {
                    var key = ReadString(element, "key");
                    var agentId = ReadString(element, "agentId");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(agentId))
                    {
                        continue;
                    }

                    accounts.Add(new WorkerAccountInventory(
                        key,
                        agentId,
                        ReadString(element, "displayName"),
                        ReadString(element, "vmName"),
                        ReadInt(element, "characterSlot")));
                }
            }

            var advertisedNodeId = nodeIdProperty.GetString()!;
            if (!string.Equals(advertisedNodeId, expectedNodeId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Ignoring inventory from host agent {ExpectedNodeId} because it advertised nodeId {AdvertisedNodeId}.",
                    expectedNodeId,
                    advertisedNodeId);
                return null;
            }

            return new WorkerInventory(
                advertisedNodeId,
                ReadString(root, "hostName"),
                agents,
                accounts,
                ReadInt(root, "vmCommandTimeoutSeconds"));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not parse worker-node inventory status.");
            return null;
        }
    }

    private bool IsLocalNode(string? nodeId)
    {
        return string.IsNullOrWhiteSpace(nodeId)
            || string.Equals(nodeId, _config.NodeId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStatusFromCurrentConnection(AgentSnapshot node)
    {
        // AgentRegistry deliberately resets this marker while retaining cached
        // inventory on every reconnect, then sets it on the first new status frame.
        return node.StatusReceivedAt is not null;
    }

    private static bool IsVmAgent(HostAgentConfig agent) =>
        string.Equals(agent.Kind, "vm", StringComparison.OrdinalIgnoreCase);

    private static bool IsHostAgent(HostAgentConfig agent) =>
        string.Equals(agent.Kind, "host", StringComparison.OrdinalIgnoreCase);

    private static AccountConfig CloneAccount(AccountConfig account, string nodeId)
    {
        return new AccountConfig
        {
            NodeId = string.IsNullOrWhiteSpace(account.NodeId) ? nodeId : account.NodeId,
            AgentId = account.AgentId,
            DisplayName = account.DisplayName,
            VmName = account.VmName,
            CharacterSlot = account.CharacterSlot
        };
    }

    private static AgentSnapshot OfflineAgent(string id, string kind, string? displayName)
    {
        return new AgentSnapshot(id, kind, displayName, null, null, false, null, null, null);
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
                ? value
                : null;
    }

    private sealed record FleetSnapshot(
        IReadOnlyDictionary<string, AccountConfig> Accounts,
        IReadOnlyDictionary<string, AgentSnapshot> Agents,
        IReadOnlyDictionary<string, HostAgentConfig> AgentConfigs,
        IReadOnlyDictionary<string, string> AgentNodes);

    private sealed record WorkerInventory(
        string NodeId,
        string? HostName,
        IReadOnlyList<WorkerAgentInventory> Agents,
        IReadOnlyList<WorkerAccountInventory> Accounts,
        int? VmCommandTimeoutSeconds);

    private sealed record WorkerAgentInventory(
        string Id,
        string Kind,
        string? DisplayName,
        string? RemoteUrl,
        AgentSnapshot Snapshot);

    private sealed record WorkerAccountInventory(
        string Key,
        string AgentId,
        string? DisplayName,
        string? VmName,
        int? CharacterSlot);
}

public sealed record FleetNodeSnapshot(
    string Id,
    string? DisplayName,
    bool IsLocal,
    bool Connected,
    string? HostName,
    string? Version,
    DateTimeOffset? LastSeenAt,
    int AgentsConnected,
    int AgentsConfigured);
