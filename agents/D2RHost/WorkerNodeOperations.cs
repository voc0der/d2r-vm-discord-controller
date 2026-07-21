using System.Text.Json;
using AgentCommon;

namespace D2RHost;

/// <summary>
/// Implements the status and command surface exposed by a worker D2RHost to its master.
/// </summary>
public sealed class WorkerNodeOperations
{
    public const int MaximumCommandDurationSeconds = 15 * 60;

    private static readonly TimeSpan DefaultAgentCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumAgentCommandTimeout = TimeSpan.FromSeconds(MaximumCommandDurationSeconds);
    private static readonly TimeSpan MaximumWorkerCommandDuration = TimeSpan.FromSeconds(MaximumCommandDurationSeconds);

    private readonly HostConfig _config;
    private readonly AgentRegistry _registry;
    private readonly HyperVOperations _hyperV;
    private readonly HostSystemOperations _system;
    private readonly MachineTelemetrySampler _telemetry = new();
    private readonly string _nodeId;

    public WorkerNodeOperations(
        HostConfig config,
        AgentRegistry registry,
        HyperVOperations hyperV,
        HostSystemOperations system)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hyperV);
        ArgumentNullException.ThrowIfNull(system);

        _nodeId = RequireConfiguredValue(config.NodeId, "nodeId");
        _config = config;
        _registry = registry;
        _hyperV = hyperV;
        _system = system;
    }

    public Task<WorkerNodeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = _registry.Snapshot();
        var agents = snapshots
            .Select(snapshot =>
            {
                _config.Agents.TryGetValue(snapshot.Id, out var metadata);
                return new WorkerNodeAgent(
                    snapshot.Id,
                    snapshot.Kind,
                    snapshot.DisplayName,
                    metadata?.RemoteUrl,
                    snapshot);
            })
            .ToArray();

        var accounts = _config.Accounts
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new WorkerNodeAccount(
                pair.Key,
                pair.Value.AgentId,
                pair.Value.DisplayName,
                pair.Value.VmName,
                pair.Value.CharacterSlot))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkerNodeStatus(
            _nodeId,
            Environment.MachineName,
            DateTimeOffset.UtcNow,
            _telemetry.Sample(),
            agents,
            accounts,
            Math.Clamp(
                _config.PowerShellTimeoutSeconds,
                10,
                MaximumCommandDurationSeconds)));
    }

    public async Task<CommandResult> HandleCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return CommandResult.Failure("Worker command is required.");
        }

        var command = request.Command.Trim().ToLowerInvariant();
        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        durationCts.CancelAfter(MaximumWorkerCommandDuration);

        try
        {
            return command switch
            {
                "agent_command" => await HandleAgentCommandAsync(request.Args, durationCts.Token),
                "vm_status" or "vm_start" or "vm_stop" or "vm_reboot" or "vm_snapshot" =>
                    await HandleHyperVCommandAsync(request with { Command = command }, durationCts.Token),
                "system_sleep" => QueueSystemAction(HostSystemPowerAction.Sleep),
                "system_shutdown" => QueueSystemAction(HostSystemPowerAction.Shutdown),
                "system_restart" => QueueSystemAction(HostSystemPowerAction.Restart),
                _ => CommandResult.Failure($"Unsupported worker command: {request.Command}")
            };
        }
        catch (WorkerCommandValidationException ex)
        {
            return CommandResult.Failure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // AgentRegistry uses InvalidOperationException for unknown/offline agents,
            // while HyperVOperations uses it for invalid or disallowed VM names. Both
            // are expected command failures rather than worker-link failures.
            return CommandResult.Failure(ex.Message);
        }
        catch (TimeoutException ex)
        {
            return CommandResult.Failure(ex.Message);
        }
        catch (OperationCanceledException) when (
            durationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Failure(
                $"Worker command exceeded the {MaximumWorkerCommandDuration.TotalMinutes:N0}-minute safety limit.");
        }
    }

    private async Task<CommandResult> HandleAgentCommandAsync(
        JsonElement args,
        CancellationToken cancellationToken)
    {
        RequireObject(args, "agent_command args");

        var agentId = RequireString(args, "agentId");
        var nestedCommand = RequireString(args, "command");
        var nestedArgs = ReadNestedArgs(args);
        var timeout = ReadAgentCommandTimeout(args);

        var result = await _registry.SendCommandAsync(
            agentId,
            nestedCommand,
            nestedArgs,
            timeout,
            cancellationToken);

        return new CommandResult(result.Ok, result.Message, result.Data);
    }

    private async Task<CommandResult> HandleHyperVCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        RequireObject(request.Args, $"{request.Command} args");
        return await _hyperV.HandleCommandAsync(request, cancellationToken);
    }

    private CommandResult QueueSystemAction(HostSystemPowerAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return CommandResult.Failure($"System power actions require Windows on node \"{_nodeId}\".");
        }

        _system.Queue(action);
        return CommandResult.Success(
            HostSystemPowerActions.FormatQueuedMessage(action),
            new
            {
                nodeId = _nodeId,
                action = action.ToString().ToLowerInvariant(),
                queued = true
            });
    }

    private static object ReadNestedArgs(JsonElement args)
    {
        if (!TryGetProperty(args, "args", out var nestedArgs)
            || nestedArgs.ValueKind is JsonValueKind.Null)
        {
            return new { };
        }

        if (nestedArgs.ValueKind != JsonValueKind.Object)
        {
            throw new WorkerCommandValidationException("agent_command args.args must be a JSON object.");
        }

        return nestedArgs.Clone();
    }

    private static TimeSpan ReadAgentCommandTimeout(JsonElement args)
    {
        if (!TryGetProperty(args, "timeoutMs", out var timeoutProperty)
            || timeoutProperty.ValueKind is JsonValueKind.Null)
        {
            return DefaultAgentCommandTimeout;
        }

        if (timeoutProperty.ValueKind != JsonValueKind.Number
            || !timeoutProperty.TryGetInt32(out var timeoutMs)
            || timeoutMs <= 0)
        {
            throw new WorkerCommandValidationException(
                "agent_command args.timeoutMs must be a positive whole number of milliseconds.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        if (timeout > MaximumAgentCommandTimeout)
        {
            throw new WorkerCommandValidationException(
                $"agent_command args.timeoutMs cannot exceed {(int)MaximumAgentCommandTimeout.TotalMilliseconds}.");
        }

        return timeout;
    }

    private static string RequireString(JsonElement args, string propertyName)
    {
        if (!TryGetProperty(args, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new WorkerCommandValidationException(
                $"agent_command args.{propertyName} must be a non-empty string.");
        }

        return property.GetString()!.Trim();
    }

    private static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkerCommandValidationException($"{description} must be a JSON object.");
        }
    }

    private static bool TryGetProperty(
        JsonElement value,
        string propertyName,
        out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty(propertyName, out property))
            {
                return true;
            }

            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string RequireConfiguredValue(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Worker mode requires {propertyName}.");
        }

        return value.Trim();
    }

    private sealed class WorkerCommandValidationException : Exception
    {
        public WorkerCommandValidationException(string message)
            : base(message)
        {
        }
    }
}
