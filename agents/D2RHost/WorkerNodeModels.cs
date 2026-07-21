using AgentCommon;

namespace D2RHost;

/// <summary>
/// The safe inventory and live status a worker publishes to its master.
/// This deliberately contains projected metadata rather than HostConfig so
/// agent and master shared secrets can never be serialized into a heartbeat.
/// </summary>
public sealed record WorkerNodeStatus(
    string NodeId,
    string HostName,
    DateTimeOffset CapturedAtUtc,
    MachineTelemetrySnapshot MachineTelemetry,
    IReadOnlyList<WorkerNodeAgent> Agents,
    IReadOnlyList<WorkerNodeAccount> Accounts,
    int VmCommandTimeoutSeconds = WorkerNodeOperations.MaximumCommandDurationSeconds);

/// <summary>
/// Public metadata and the current connection snapshot for one worker-local agent.
/// </summary>
public sealed record WorkerNodeAgent(
    string Id,
    string Kind,
    string? DisplayName,
    string? RemoteUrl,
    AgentSnapshot Snapshot);

/// <summary>
/// Account routing metadata needed by the master. No credentials are included.
/// </summary>
public sealed record WorkerNodeAccount(
    string Key,
    string AgentId,
    string? DisplayName,
    string? VmName,
    int? CharacterSlot);
