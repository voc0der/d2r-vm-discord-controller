using AgentCommon;

namespace D2RAgent;

public static class VmAgentConfigWizard
{
    public static VmAgentConfig LoadOrCreate(string configPath)
    {
        if (File.Exists(configPath))
        {
            return ConfigLoader.Load<VmAgentConfig>(configPath);
        }

        if (!ConsolePrompt.CanPrompt)
        {
            throw new FileNotFoundException($"VM agent config was not found: {configPath}", configPath);
        }

        Console.Title = "D2R VM Agent Setup";
        Console.WriteLine("D2RAgent first-run setup");
        Console.WriteLine($"No config file was found at: {configPath}");
        Console.WriteLine("Use the agentId and sharedSecret printed by D2RHost setup.");
        Console.WriteLine();

        var config = new VmAgentConfig
        {
            AgentId = ConsolePrompt.ReadString("VM agent ID", DefaultAgentId(), allowEmpty: false),
            ControllerUrl = ReadControllerUrl(null),
            SharedSecret = ConsolePrompt.ReadString("Shared secret from D2RHost", allowEmpty: false),
            HeartbeatSeconds = ConsolePrompt.ReadInt("Heartbeat interval, seconds", 15, minValue: 5),
            BattleNetPath = ConsolePrompt.ReadString(
                "Battle.net executable path",
                @"C:\Program Files (x86)\Battle.net\Battle.net.exe",
                allowEmpty: false),
            BattleNetArgs = ConsolePrompt.ReadString("Battle.net D2R launch args", "--exec=\"launch OSI\"", allowEmpty: false),
            PreferBattleNetExecLaunch = true,
            D2RPath = NullIfBlank(ConsolePrompt.ReadString("Direct D2R executable path, optional advanced override", allowEmpty: true))
        };

        ConfigLoader.Save(configPath, config);
        Console.WriteLine($"Wrote VM agent config: {configPath}");
        Console.WriteLine();
        return config;
    }

    public static async Task<VmAgentConfig> EnsureConnectsAsync(
        string configPath,
        VmAgentConfig config,
        Func<VmAgentConfig, CancellationToken, Task> probeAsync,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                Console.WriteLine($"Testing connection to {config.ControllerUrl} as {config.AgentId}...");
                await probeAsync(config, cancellationToken);
                Console.WriteLine("Connection test passed.");
                Console.WriteLine();
                return config;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Connection test failed: {ex.Message}");
                if (!ConsolePrompt.CanPrompt || !ConsolePrompt.ReadBool("Update connection settings and retry", true))
                {
                    Console.WriteLine("Starting reconnect loop with the current config.");
                    Console.WriteLine();
                    return config;
                }

                UpdateConnectionSettings(config);
                ConfigLoader.Save(configPath, config);
                Console.WriteLine($"Updated VM agent config: {configPath}");
                Console.WriteLine();
            }
        }
    }

    private static void UpdateConnectionSettings(VmAgentConfig config)
    {
        config.ControllerUrl = ReadControllerUrl(config.ControllerUrl);

        if (ConsolePrompt.ReadBool("Update agent ID or shared secret too", false))
        {
            config.AgentId = ConsolePrompt.ReadString("VM agent ID", config.AgentId, allowEmpty: false);
            config.SharedSecret = ConsolePrompt.ReadString("Shared secret from D2RHost", config.SharedSecret, allowEmpty: false);
        }
    }

    private static string ReadControllerUrl(string? currentUrl)
    {
        var (currentHost, currentPort) = ParseControllerUrl(currentUrl);
        var hostOrUrl = ConsolePrompt.ReadString("D2RHost hostname/IP or ws:// URL", currentHost, allowEmpty: false);

        if (Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)))
        {
            var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
                ? "/agent"
                : uri.AbsolutePath;
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
        }

        var port = ConsolePrompt.ReadInt("D2RHost port", currentPort, minValue: 1, maxValue: 65535);
        return $"ws://{hostOrUrl}:{port}/agent";
    }

    private static (string Host, int Port) ParseControllerUrl(string? controllerUrl)
    {
        if (!string.IsNullOrWhiteSpace(controllerUrl)
            && Uri.TryCreate(controllerUrl, UriKind.Absolute, out var uri)
            && uri.Port > 0)
        {
            return (uri.Host, uri.Port);
        }

        return ("d2r-host", 8080);
    }

    private static string DefaultAgentId()
    {
        return Environment.MachineName.ToLowerInvariant();
    }

    private static string? NullIfBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
