using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCommon;

namespace D2RHost;

public sealed class HyperVOperations
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

        return request.Command switch
        {
            "vm_status" => await RunForVmAsync(vmName, GetVmStatusCommand(vmName), cancellationToken),
            "vm_start" => await RunForVmAsync(vmName, $"Start-VM -Name {PsQuote(vmName)} | Out-Null; {GetVmStatusCommand(vmName)}", cancellationToken),
            "vm_stop" => await RunForVmAsync(vmName, $"Stop-VM -Name {PsQuote(vmName)} -Force | Out-Null; {GetVmStatusCommand(vmName)}", cancellationToken),
            "vm_reboot" => await RunForVmAsync(vmName, $"Restart-VM -Name {PsQuote(vmName)} -Force | Out-Null; {GetVmStatusCommand(vmName)}", cancellationToken),
            "vm_snapshot" => await SnapshotVmAsync(vmName, request.Args, cancellationToken),
            _ => CommandResult.Failure($"Unsupported Hyper-V command: {request.Command}")
        };
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

    private static string GetVmStatusCommand(string vmName)
    {
        return $"Get-VM -Name {PsQuote(vmName)} | Select-Object Name,State,Uptime,CPUUsage,MemoryAssigned | ConvertTo-Json -Compress";
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
