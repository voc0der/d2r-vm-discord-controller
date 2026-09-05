using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCommon;

namespace D2RHost;

public sealed class HyperVOperations : ILocalVmPowerOperations
{
    private readonly HostConfig _config;

    public HyperVOperations(HostConfig config)
    {
        _config = config;
    }

    public Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<object>(new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            hyperVHost = true,
            timeUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task<CommandResult> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var vmName = RequireString(request.Args, "vmName");
        EnsureAllowedVmName(vmName);

        if (request.Command == "vm_snapshot")
        {
            return await SnapshotVmAsync(vmName, request.Args, cancellationToken);
        }

        var script = TryBuildVmPowerScript(request.Command, vmName);
        return script is null
            ? CommandResult.Failure($"Unsupported Hyper-V command: {request.Command}")
            : await RunForVmAsync(vmName, script, cancellationToken);
    }

    /// <summary>
    /// Builds the PowerShell one-liner behind each VM power verb, or null for a command this class
    /// does not own. Every one of them ends by re-reading the VM's status, so a caller always gets
    /// the post-action state back in the same round trip.
    /// </summary>
    /// <remarks>
    /// The four verbs are deliberately distinct cmdlets rather than combinations of each other,
    /// because they ask the guest for progressively less:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <c>vm_reboot</c> is one <c>Restart-VM</c>. It is NOT a stop followed by a start, and must not
    /// become one - the guest stays powered on across it, so it is the cheapest way back for a guest
    /// that is still answering its integration services, and it never passes through the Off state
    /// where a separate Start-VM could fail and strand the VM.
    /// </description></item>
    /// <item><description>
    /// <c>vm_stop</c> is <c>Stop-VM -Force</c>, which despite the name still asks the guest through
    /// its integration services; <c>-Force</c> only suppresses the confirmation prompt.
    /// </description></item>
    /// <item><description>
    /// <c>vm_turnoff</c> is the hypervisor equivalent of holding the power button. <c>-TurnOff</c>
    /// needs nothing from the guest at all - which is the entire point, because the guests it exists
    /// for are frozen on the Windows boot logo and are not running integration services to ask.
    /// Never a first resort: see VmHangRecoveryPolicy for when the host is allowed to use it.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static string? TryBuildVmPowerScript(string command, string vmName)
    {
        var status = GetVmStatusCommand(vmName);
        var name = PsQuote(vmName);
        return command switch
        {
            "vm_status" => status,
            "vm_start" => $"Start-VM -Name {name} | Out-Null; {status}",
            "vm_stop" => $"Stop-VM -Name {name} -Force | Out-Null; {status}",
            "vm_turnoff" => $"Stop-VM -Name {name} -TurnOff -Force | Out-Null; {status}",
            "vm_reboot" => $"Restart-VM -Name {name} -Force | Out-Null; {status}",
            _ => null
        };
    }

    public async Task<VmPowerStateResult> GetPowerStateAsync(
        string vmName,
        CancellationToken cancellationToken)
    {
        EnsureAllowedVmName(vmName);
        var result = await RunPowerShellAsync(
            $"$vm = Get-VM -Name {PsQuote(vmName)} -ErrorAction Stop; Write-Output ([string]$vm.State)",
            cancellationToken);
        return result.Ok
            ? VmPowerStateResult.Success(result.Message.Trim())
            : VmPowerStateResult.Failure(result.Message);
    }

    public async Task<CommandResult> StopAsync(string vmName, CancellationToken cancellationToken)
    {
        EnsureAllowedVmName(vmName);
        return await RunForVmAsync(
            vmName,
            $"Stop-VM -Name {PsQuote(vmName)} -Force -ErrorAction Stop | Out-Null; {GetVmStatusCommand(vmName)}",
            cancellationToken);
    }

    public async Task<CommandResult> StartAsync(string vmName, CancellationToken cancellationToken)
    {
        EnsureAllowedVmName(vmName);
        return await RunForVmAsync(
            vmName,
            $"Start-VM -Name {PsQuote(vmName)} -ErrorAction Stop | Out-Null; {GetVmStatusCommand(vmName)}",
            cancellationToken);
    }

    private async Task<CommandResult> SnapshotVmAsync(
        string vmName,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var snapshotName = TryGetString(args, "snapshotName");
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            snapshotName = $"d2r-ops-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        }

        var command = $"Checkpoint-VM -Name {PsQuote(vmName)} -SnapshotName {PsQuote(snapshotName)} | Out-Null; {GetVmStatusCommand(vmName)}";
        var result = await RunForVmAsync(vmName, command, cancellationToken);
        return result.Ok
            ? result with { Message = $"Snapshot created for {vmName}: {snapshotName}" }
            : result;
    }

    private async Task<CommandResult> RunForVmAsync(
        string vmName,
        string command,
        CancellationToken cancellationToken)
    {
        var result = await RunPowerShellAsync(command, cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        return CommandResult.Success($"{vmName}: {result.Message}", result.Data);
    }

    private async Task<CommandResult> RunPowerShellAsync(
        string command,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _config.PowerShellPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                error.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.PowerShellTimeoutSeconds, 10)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return CommandResult.Failure($"PowerShell command timed out after {_config.PowerShellTimeoutSeconds}s.");
        }

        var stdout = output.ToString().Trim();
        var stderr = error.ToString().Trim();

        if (process.ExitCode != 0)
        {
            return CommandResult.Failure(string.IsNullOrWhiteSpace(stderr) ? $"PowerShell exited {process.ExitCode}." : stderr);
        }

        return CommandResult.Success(string.IsNullOrWhiteSpace(stdout) ? "PowerShell command completed." : stdout, new { output = stdout });
    }

    private void EnsureAllowedVmName(string vmName)
    {
        if (_config.AllowedVmNamePrefixes.Length == 0)
        {
            return;
        }

        if (_config.AllowedVmNamePrefixes.Any(prefix => vmName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InvalidOperationException($"VM name is not allowed by config: {vmName}");
    }

    private static string RequireString(JsonElement args, string propertyName)
    {
        var value = TryGetString(args, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{propertyName} is required.");
        }

        return value;
    }

    private static string? TryGetString(JsonElement args, string propertyName)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    /// <summary>
    /// Adds Hyper-V's Heartbeat integration-service status to the VM status payload. That is the
    /// one field that tells the host whether a powered-on guest actually reached a working Windows,
    /// which is what lets VmHangRecoveryPolicy tell a frozen boot from a slow one instead of
    /// guessing from elapsed time. SilentlyContinue plus the null coalesce matter: the service can
    /// be disabled per-VM or missing entirely, and that must serialize as an absent heartbeat
    /// (treated as "no evidence") rather than failing the whole status call.
    /// </summary>
    private static string GetVmStatusCommand(string vmName)
    {
        return "$vm = Get-VM -Name " + PsQuote(vmName) + "; "
            + "$hb = Get-VMIntegrationService -VMName " + PsQuote(vmName) + " -Name 'Heartbeat' -ErrorAction SilentlyContinue; "
            + "[pscustomobject]@{ "
            + "Name = $vm.Name; State = [string]$vm.State; Uptime = $vm.Uptime; "
            + "CPUUsage = $vm.CPUUsage; MemoryAssigned = $vm.MemoryAssigned; "
            + "Heartbeat = $(if ($hb) { [string]$hb.PrimaryStatusDescription } else { $null }); "
            + "HeartbeatEnabled = $(if ($hb) { [bool]$hb.Enabled } else { $false }) "
            + "} | ConvertTo-Json -Compress";
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }
}
