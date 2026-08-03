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
    int VmCommandTimeoutSeconds = WorkerNodeOperations.MaximumCommandDurationSeconds,
    // Things the worker needs to tell an operator but cannot say itself, because Discord lives on
    // the master. A failed sleep is the motivating case: it happens after the command has already
    // answered, so there is no command result left to fail, and the worker's own log is on the
    // machine nobody is looking at. Defaulted so an older master simply ignores the field.
    IReadOnlyList<string>? Alerts = null,
    // Explicit and fail-closed. A current master will not infer support from a version string,
    // and an older worker that omits this field cannot receive any physical-host power action.
    bool VmSafeHostPowerTransitions = false,
    // A safety-sensitive nested command can be bound to the exact VM-agent connection whose
    // status authorized it. Older workers ignore the forwarded generation, so the master must
    // require this explicit capability before tunneling such a command.
    bool GenerationBoundAgentCommands = false);

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
