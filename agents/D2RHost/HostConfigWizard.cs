using AgentCommon;

namespace D2RHost;

public static class HostConfigWizard
{
    public static HostConfig Create(string configPath)
    {
        Console.Title = "D2R Host Controller Setup";
        Console.WriteLine("D2RHost first-run setup");
        Console.WriteLine($"No config file was found at: {configPath}");
        Console.WriteLine("Answer these once and I will write the JSON for future launches.");
        Console.WriteLine();

        var config = new HostConfig
        {
            HttpPort = ConsolePrompt.ReadInt("HTTP/WebSocket port", 8080, minValue: 1, maxValue: 65535),
            DatabasePath = ConsolePrompt.ReadString("SQLite database path", DefaultDatabasePath(configPath), allowEmpty: false),
            StartAllDelaySeconds = ConsolePrompt.ReadInt("Delay between all-client commands, seconds", 20, minValue: 0),
            PowerShellPath = ConsolePrompt.ReadString("PowerShell path", "powershell.exe", allowEmpty: false),
            PowerShellTimeoutSeconds = ConsolePrompt.ReadInt("PowerShell timeout, seconds", 90, minValue: 10),
            AllowedVmNamePrefixes = ConsolePrompt.ReadCsv("Allowed Hyper-V VM name prefixes, comma-separated", "d2r-")
        };

        var enableDiscord = ConsolePrompt.ReadBool("Enable Discord bot", true);
        config.DisableDiscord = !enableDiscord;
        if (enableDiscord)
        {
            config.DiscordToken = ConsolePrompt.ReadString(
                "Discord bot token",
                Environment.GetEnvironmentVariable("DISCORD_TOKEN"),
                allowEmpty: false);
            config.DiscordGuildId = ConsolePrompt.ReadOptionalUlong("Discord guild ID for instant command registration");
            config.AllowedDiscordUserIds = ConsolePrompt.ReadCsv("Allowed Discord user IDs, comma-separated");
            config.GuildChannel = ConsolePrompt.ReadOptionalUlong("Discord notification channel ID");
            if (config.GuildChannel is not null)
            {
                config.GameSessionNotificationsEnabled = ConsolePrompt.ReadBool("Post game session notifications", false);
                config.UpdateNotificationsEnabled = ConsolePrompt.ReadBool("Post host availability/update notifications", true);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Add the D2R VM accounts this host should control.");
        var nextIndex = 1;
        do
        {
            AddAccount(config, nextIndex);
            nextIndex++;
        }
        while (ConsolePrompt.ReadBool("Add another VM/account", nextIndex == 2));

        Console.WriteLine();
        Console.WriteLine("Config preview for VM agents:");
        foreach (var (accountKey, account) in config.Accounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var agent = config.Agents[account.AgentId];
            Console.WriteLine($"- {accountKey}: agentId={account.AgentId}, controllerUrl=ws://{Environment.MachineName}:{config.HttpPort}/agent, sharedSecret={agent.SharedSecret}");
        }

        Console.WriteLine();
        return config;
    }

    private static void AddAccount(HostConfig config, int index)
    {
        var defaultAccountKey = $"hc{index}";
        var accountKey = ConsolePrompt.ReadString("Account key for Discord commands", defaultAccountKey, allowEmpty: false);
        while (config.Accounts.ContainsKey(accountKey))
        {
            Console.WriteLine($"Account key already exists: {accountKey}");
            accountKey = ConsolePrompt.ReadString("Account key for Discord commands", defaultAccountKey, allowEmpty: false);
        }

        var vmName = ConsolePrompt.ReadString("Hyper-V VM name", $"d2r-hc-{index:00}", allowEmpty: false);
        var agentId = ConsolePrompt.ReadString("VM agent ID", vmName, allowEmpty: false);
        while (config.Agents.ContainsKey(agentId))
        {
            Console.WriteLine($"Agent ID already exists: {agentId}");
            agentId = ConsolePrompt.ReadString("VM agent ID", vmName, allowEmpty: false);
        }

        var displayName = ConsolePrompt.ReadString("Display name", vmName, allowEmpty: false);
        var characterSlot = ConsolePrompt.ReadInt("Default character slot for this account", 1, minValue: 1, maxValue: 8);
        var sharedSecret = ConsolePrompt.ReadString("Shared secret for this VM agent", SecretGenerator.Create(), allowEmpty: false);
        var remoteUrl = ConsolePrompt.ReadString("Remote-control URL, optional", allowEmpty: true);

        config.Agents[agentId] = new HostAgentConfig
        {
            Kind = "vm",
            DisplayName = displayName,
            SharedSecret = sharedSecret,
            RemoteUrl = string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl
        };

        config.Accounts[accountKey] = new AccountConfig
        {
            AgentId = agentId,
            DisplayName = displayName,
            VmName = vmName,
            CharacterSlot = characterSlot
        };
    }

    private static string DefaultDatabasePath(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        return string.IsNullOrWhiteSpace(directory)
            ? @"C:\D2ROps\d2r-host.sqlite"
            : Path.Combine(directory, "d2r-host.sqlite");
    }
}
