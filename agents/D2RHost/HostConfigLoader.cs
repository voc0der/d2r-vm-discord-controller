using System.Text.Json;

namespace D2RHost;

public static class HostConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static HostConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"D2R host config was not found: {path}", path);
        }

        var config = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"D2R host config was empty or invalid: {path}");

        ApplyEnvironment(config);
        Validate(config);
        return config;
    }

    private static void ApplyEnvironment(HostConfig config)
    {
        config.DiscordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
            ?? config.DiscordToken;

        if (ulong.TryParse(Environment.GetEnvironmentVariable("DISCORD_GUILD_ID"), out var guildId))
        {
            config.DiscordGuildId = guildId;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("DISABLE_DISCORD"), out var disableDiscord))
        {
            config.DisableDiscord = disableDiscord;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("HTTP_PORT"), out var httpPort))
        {
            config.HttpPort = httpPort;
        }

        config.DatabasePath = Environment.GetEnvironmentVariable("DB_PATH")
            ?? config.DatabasePath;

        if (int.TryParse(Environment.GetEnvironmentVariable("CLIENT_STAGGER_SECONDS"), out var staggerSeconds)
            && staggerSeconds >= 0)
        {
            config.ClientStaggerSeconds = staggerSeconds;
        }
    }

    private static void Validate(HostConfig config)
    {
        if (!config.DisableDiscord && string.IsNullOrWhiteSpace(config.DiscordToken))
        {
            throw new InvalidOperationException("DISCORD_TOKEN is required unless disableDiscord is true.");
        }

        foreach (var (accountKey, account) in config.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AgentId))
            {
                throw new InvalidOperationException($"Account \"{accountKey}\" is missing agentId.");
            }

            if (!config.Agents.ContainsKey(account.AgentId))
            {
                throw new InvalidOperationException($"Account \"{accountKey}\" references missing VM agent \"{account.AgentId}\".");
            }
        }

        foreach (var (agentId, agent) in config.Agents)
        {
            if (!string.Equals(agent.Kind, "vm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Agent \"{agentId}\" must have kind \"vm\". Hyper-V control now runs locally in D2RHost.");
            }

            if (string.IsNullOrWhiteSpace(agent.SharedSecret) || agent.SharedSecret.Length < 12)
            {
                throw new InvalidOperationException($"Agent \"{agentId}\" needs a sharedSecret of at least 12 characters.");
            }
        }
    }
}
