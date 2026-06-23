using System.Diagnostics;
using System.Text;

namespace AgentCommon;

public sealed record WindowsFirewallRuleSpec(
    string Name,
    string Direction,
    string Protocol,
    int? LocalPort,
    int? RemotePort,
    string? RemoteIp,
    string? ProgramPath);

public sealed record WindowsFirewallSelfHealResult(
    bool Supported,
    bool Ok,
    bool Changed,
    string RuleName,
    string Message);

public static class WindowsFirewallSelfHeal
{
    private const string HostRulePrefix = "D2ROps Host inbound TCP";
    private const string AgentRulePrefix = "D2ROps Agent outbound TCP";

    public static WindowsFirewallSelfHealResult EnsureHostInboundTcp(
        int localPort,
        Action<string>? log = null)
    {
        var spec = BuildHostInboundTcpRule(localPort, GetCurrentProcessPath());
        return EnsureRuleExists(spec, log);
    }

    public static WindowsFirewallSelfHealResult EnsureAgentControllerOutboundTcp(
        string controllerUrl,
        Action<string>? log = null)
    {
        if (!TryBuildAgentControllerOutboundTcpRule(controllerUrl, GetCurrentProcessPath(), out var spec, out var message))
        {
            var result = new WindowsFirewallSelfHealResult(
                Supported: OperatingSystem.IsWindows(),
                Ok: false,
                Changed: false,
                RuleName: "",
                Message: message);
            log?.Invoke($"Firewall self-heal skipped: {message}");
            return result;
        }

        return EnsureRuleExists(spec, log);
    }

    public static WindowsFirewallRuleSpec BuildHostInboundTcpRule(int localPort, string? programPath)
    {
        ValidateTcpPort(localPort, nameof(localPort));
        return new WindowsFirewallRuleSpec(
            Name: $"{HostRulePrefix} {localPort}",
            Direction: "in",
            Protocol: "TCP",
            LocalPort: localPort,
            RemotePort: null,
            RemoteIp: null,
            ProgramPath: NormalizeProgramPath(programPath));
    }

    public static bool TryBuildAgentControllerOutboundTcpRule(
        string controllerUrl,
        string? programPath,
        out WindowsFirewallRuleSpec spec,
        out string message)
    {
        spec = default!;
        if (!Uri.TryCreate(controllerUrl, UriKind.Absolute, out var uri))
        {
            message = $"ControllerUrl is not an absolute URI: {controllerUrl}";
            return false;
        }

        var port = uri.IsDefaultPort
            ? string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                    ? 443
                    : 80
            : uri.Port;
        if (!IsValidTcpPort(port))
        {
            message = $"ControllerUrl port is not valid: {port}";
            return false;
        }

        var remoteIp = TryGetFirewallRemoteIp(uri.Host);
        var hostSuffix = SanitizeRuleNamePart(uri.Host);
        spec = new WindowsFirewallRuleSpec(
            Name: $"{AgentRulePrefix} {hostSuffix} {port}",
            Direction: "out",
            Protocol: "TCP",
            LocalPort: null,
            RemotePort: port,
            RemoteIp: remoteIp,
            ProgramPath: NormalizeProgramPath(programPath));
        message = remoteIp is null
            ? $"Controller host {uri.Host} is not a literal IP; outbound rule will allow TCP {port} to any remote IP."
            : $"Controller host {uri.Host} resolved as firewall remote IP {remoteIp}.";
        return true;
    }

    public static string[] BuildNetshAddRuleArguments(WindowsFirewallRuleSpec spec)
    {
        var args = new List<string>
        {
            "advfirewall",
            "firewall",
            "add",
            "rule",
            $"name={spec.Name}",
            $"dir={spec.Direction}",
            "action=allow",
            "enable=yes",
            "profile=any",
            $"protocol={spec.Protocol}"
        };

        if (spec.LocalPort is { } localPort)
        {
            args.Add($"localport={localPort}");
        }

        if (spec.RemotePort is { } remotePort)
        {
            args.Add($"remoteport={remotePort}");
        }

        if (!string.IsNullOrWhiteSpace(spec.RemoteIp))
        {
            args.Add($"remoteip={spec.RemoteIp}");
        }

        if (!string.IsNullOrWhiteSpace(spec.ProgramPath))
        {
            args.Add($"program={spec.ProgramPath}");
        }

        return args.ToArray();
    }

    private static WindowsFirewallSelfHealResult EnsureRuleExists(
        WindowsFirewallRuleSpec spec,
        Action<string>? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsFirewallSelfHealResult(
                Supported: false,
                Ok: true,
                Changed: false,
                RuleName: spec.Name,
                Message: "Windows firewall self-heal skipped on non-Windows OS.");
        }

        var existing = RunNetsh(["advfirewall", "firewall", "show", "rule", $"name={spec.Name}"]);
        if (existing.ExitCode == 0)
        {
            var existingMessage = $"Windows firewall rule already exists: {spec.Name}";
            log?.Invoke(existingMessage);
            return new WindowsFirewallSelfHealResult(
                Supported: true,
                Ok: true,
                Changed: false,
                RuleName: spec.Name,
                Message: existingMessage);
        }

        var added = RunNetsh(BuildNetshAddRuleArguments(spec));
        if (added.ExitCode == 0)
        {
            var createdMessage = $"Created Windows firewall rule: {spec.Name}";
            log?.Invoke(createdMessage);
            return new WindowsFirewallSelfHealResult(
                Supported: true,
                Ok: true,
                Changed: true,
                RuleName: spec.Name,
                Message: createdMessage);
        }

        var failure = new StringBuilder();
        failure.Append($"Could not create Windows firewall rule {spec.Name}. ");
        failure.Append("Run the process elevated or add the rule manually. ");
        if (!string.IsNullOrWhiteSpace(added.StandardError))
        {
            failure.Append(added.StandardError.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(added.StandardOutput))
        {
            failure.Append(added.StandardOutput.Trim());
        }
        else
        {
            failure.Append($"netsh exited with code {added.ExitCode}.");
        }

        var message = failure.ToString();
        log?.Invoke($"Firewall self-heal failed: {message}");
        return new WindowsFirewallSelfHealResult(
            Supported: true,
            Ok: false,
            Changed: false,
            RuleName: spec.Name,
            Message: message);
    }

    private static NetshResult RunNetsh(IEnumerable<string> arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new NetshResult(-1, output, "netsh timed out after 10s.");
            }

            return new NetshResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new NetshResult(-1, "", ex.Message);
        }
    }

    private static string? GetCurrentProcessPath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(mainModulePath)
            ? null
            : mainModulePath;
    }

    private static string? NormalizeProgramPath(string? programPath)
    {
        return string.IsNullOrWhiteSpace(programPath)
            ? null
            : programPath.Trim();
    }

    private static void ValidateTcpPort(int port, string parameterName)
    {
        if (!IsValidTcpPort(port))
        {
            throw new ArgumentOutOfRangeException(parameterName, port, "TCP port must be between 1 and 65535.");
        }
    }

    private static bool IsValidTcpPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static string? TryGetFirewallRemoteIp(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            return address.ToString();
        }

        return null;
    }

    private static string SanitizeRuleNamePart(string value)
    {
        var sanitized = new string(value
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "controller" : sanitized;
    }

    private sealed record NetshResult(int ExitCode, string StandardOutput, string StandardError);
}
