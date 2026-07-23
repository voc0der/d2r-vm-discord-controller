using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCommon;

namespace D2RHost;

public static class HostConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static HostConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            if (!ConsolePrompt.CanPrompt)
            {
                throw new FileNotFoundException($"D2R host config was not found: {path}", path);
            }

            var config = HostConfigWizard.Create(path);
            Save(path, config);
            Console.WriteLine($"Wrote host config: {path}");
            Console.WriteLine("Starting D2RHost with the saved config.");
            Console.WriteLine();
        }

        return Load(path);
    }

    public static HostConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"D2R host config was not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        ValidateIdentityMapKeys(json);
        var config = JsonSerializer.Deserialize<HostConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"D2R host config was empty or invalid: {path}");

        ApplyEnvironment(config);
        Validate(
            config,
            HasJsonProperty(json, "nodeId"),
            HasJsonProperty(json, "windowsFirewall"),
            CreateFirewallOwnerId(path));
        return config;
    }

    public static void Save(string path, HostConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = JsonSerializer.SerializeToNode(config, WriteOptions)
            ?? throw new InvalidOperationException("Could not serialize the D2R host config.");
        if (!config.WindowsFirewall.WasExplicitlyConfigured && document is JsonObject root)
        {
            // Preserve the old config's compatibility behavior across unrelated saves.
            // Adding this section is the operator's explicit opt-in to scoped rules.
            root.Remove("windowsFirewall");
        }

        var json = document.ToJsonString(WriteOptions);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    public static IReadOnlyList<string> GetWarnings(HostConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var warnings = new List<string>();
        var vmAgentIds = config.Agents
            .Where(pair => string.Equals(pair.Value.Kind, "vm", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .OrderBy(agentId => agentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accountsByAgent = config.Accounts
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.AgentId))
            .GroupBy(pair => pair.Value.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => pair.Key)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var unmappedVmAgentIds = vmAgentIds
            .Where(agentId => !accountsByAgent.ContainsKey(agentId))
            .ToArray();
        if (unmappedVmAgentIds.Length > 0)
        {
            warnings.Add(
                $"VM agent(s) without account mappings: {string.Join(", ", unmappedVmAgentIds)}. "
                + "Fleet-wide client commands only target entries under accounts.");
        }

        var duplicateMappings = accountsByAgent
            .Where(pair => pair.Value.Length > 1)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key} <- {string.Join(", ", pair.Value)}")
            .ToArray();
        if (duplicateMappings.Length > 0)
        {
            warnings.Add(
                "Multiple accounts map to the same VM agent and can dispatch duplicate commands: "
                + $"{string.Join("; ", duplicateMappings)}.");
        }

        return warnings;
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

    private static void Validate(
        HostConfig config,
        bool nodeIdWasSpecified,
        bool windowsFirewallWasSpecified,
        string firewallOwnerId)
    {
        if (string.IsNullOrWhiteSpace(config.Mode))
        {
            throw new InvalidOperationException("mode must be either \"master\" or \"worker\".");
        }

        config.Mode = config.Mode.Trim().ToLowerInvariant();
        if (!config.IsMaster && !config.IsWorker)
        {
            throw new InvalidOperationException("mode must be either \"master\" or \"worker\".");
        }

        if (config.IsMaster && string.IsNullOrWhiteSpace(config.NodeId))
        {
            // Configs written before node roles existed represent the local master.
            config.NodeId = "local";
        }

        if (config.IsWorker && (!nodeIdWasSpecified || string.IsNullOrWhiteSpace(config.NodeId)))
        {
            throw new InvalidOperationException("nodeId is required when mode is \"worker\".");
        }

        if (string.IsNullOrWhiteSpace(config.NodeId))
        {
            throw new InvalidOperationException("nodeId cannot be empty.");
        }

        config.NodeId = config.NodeId.Trim();
        config.Agents = NormalizeDictionary(config.Agents, "agent");
        config.Accounts = NormalizeDictionary(config.Accounts, "account");

        if (config.NodeHeartbeatSeconds < 1)
        {
            throw new InvalidOperationException("nodeHeartbeatSeconds must be at least 1.");
        }

        if (config.AgentOfflineAfterSeconds < 15)
        {
            throw new InvalidOperationException("agentOfflineAfterSeconds must be at least 15.");
        }

        if (config.HttpPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("httpPort must be between 1 and 65535.");
        }

        config.WindowsFirewall ??= new WindowsFirewallConfig();
        config.WindowsFirewall.WasExplicitlyConfigured = windowsFirewallWasSpecified;
        config.WindowsFirewall.OwnerId = firewallOwnerId;
        if (config.WindowsFirewall.ReconcileSeconds is < 5 or > 3600)
        {
            throw new InvalidOperationException(
                "windowsFirewall.reconcileSeconds must be between 5 and 3600.");
        }

        if (config.WindowsFirewall.Manage)
        {
            config.WindowsFirewall.TrustedNetworks = NormalizeTrustedNetworks(
                config.WindowsFirewall.TrustedNetworks);
        }
        else
        {
            config.WindowsFirewall.TrustedNetworks ??= [];
        }

        if (config.IsWorker)
        {
            // A worker is controlled by its master and never owns a Discord session.
            config.DisableDiscord = true;

            if (!IsAbsoluteWebSocketUrl(config.MasterUrl))
            {
                throw new InvalidOperationException(
                    "masterUrl is required in worker mode and must be an absolute ws:// or wss:// URL.");
            }

            if (string.IsNullOrWhiteSpace(config.MasterSharedSecret)
                || config.MasterSharedSecret.Length < 12)
            {
                throw new InvalidOperationException(
                    "masterSharedSecret must be at least 12 characters when mode is \"worker\".");
            }

            config.MasterUrl = config.MasterUrl!.Trim();
        }
        else if (!config.DisableDiscord && string.IsNullOrWhiteSpace(config.DiscordToken))
        {
            throw new InvalidOperationException("DISCORD_TOKEN is required unless disableDiscord is true.");
        }

        foreach (var (agentId, agent) in config.Agents)
        {
            var isVm = string.Equals(agent.Kind, "vm", StringComparison.OrdinalIgnoreCase);
            var isHost = string.Equals(agent.Kind, "host", StringComparison.OrdinalIgnoreCase);

            // Preserve an old single-host config whose VM agent happened to be named
            // "local": before node roles existed, that implicit default occupied no
            // routing namespace. Explicit node IDs and every worker must be unique.
            if (string.Equals(agentId, config.NodeId, StringComparison.OrdinalIgnoreCase)
                && (nodeIdWasSpecified || config.IsWorker || isHost))
            {
                throw new InvalidOperationException(
                    $"Agent ID \"{agentId}\" conflicts with this host's nodeId; node and VM/host agent IDs must be globally unique.");
            }

            if (!isVm && !(config.IsMaster && isHost))
            {
                var expected = config.IsMaster ? "\"vm\" or \"host\"" : "\"vm\"";
                throw new InvalidOperationException(
                    $"Agent \"{agentId}\" must have kind {expected} in {config.Mode} mode.");
            }

            if (string.IsNullOrWhiteSpace(agent.SharedSecret) || agent.SharedSecret.Length < 12)
            {
                throw new InvalidOperationException($"Agent \"{agentId}\" needs a sharedSecret of at least 12 characters.");
            }
        }

        foreach (var (accountKey, account) in config.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AgentId))
            {
                throw new InvalidOperationException($"Account \"{accountKey}\" is missing agentId.");
            }

            if (!config.Agents.TryGetValue(account.AgentId, out var agent))
            {
                throw new InvalidOperationException($"Account \"{accountKey}\" references missing VM agent \"{account.AgentId}\".");
            }

            if (!string.Equals(agent.Kind, "vm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Account \"{accountKey}\" must reference a VM agent; \"{account.AgentId}\" has kind \"{agent.Kind}\".");
            }

            if (string.IsNullOrWhiteSpace(account.NodeId))
            {
                account.NodeId = config.NodeId;
            }
            else if (!string.Equals(account.NodeId.Trim(), config.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Account \"{accountKey}\" belongs to node \"{account.NodeId}\", but this host is node \"{config.NodeId}\".");
            }

            account.NodeId = config.NodeId;
        }
    }

    private static bool IsAbsoluteWebSocketUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.Host)
            && uri.Port is >= 1 and <= 65535
            && (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] NormalizeTrustedNetworks(string[]? source)
    {
        if (source is null || source.Length == 0)
        {
            throw new InvalidOperationException(
                "windowsFirewall.trustedNetworks must contain LocalSubnet or at least one IP address/CIDR.");
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawValue in source)
        {
            var value = rawValue?.Trim() ?? "";
            if (string.Equals(value, "LocalSubnet", StringComparison.OrdinalIgnoreCase))
            {
                value = "LocalSubnet";
            }
            else if (!TryNormalizeIpAddressOrCidr(
                         value,
                         out var normalizedValue,
                         out var isUnrestricted))
            {
                throw new InvalidOperationException(
                    $"windowsFirewall.trustedNetworks contains invalid value \"{value}\"; use LocalSubnet, an IP address, or CIDR.");
            }
            else if (isUnrestricted)
            {
                throw new InvalidOperationException(
                    "windowsFirewall.trustedNetworks cannot allow every address; use manage=false when firewall policy is managed externally.");
            }
            else
            {
                value = normalizedValue;
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryNormalizeIpAddressOrCidr(
        string value,
        out string normalized,
        out bool isUnrestricted)
    {
        normalized = "";
        isUnrestricted = false;

        if (IPAddress.TryParse(value, out var singleAddress))
        {
            normalized = singleAddress.ToString();
            return true;
        }

        var slash = value.IndexOf('/');
        if (slash <= 0
            || slash == value.Length - 1
            || slash != value.LastIndexOf('/')
            || !IPAddress.TryParse(value[..slash], out var address)
            || !int.TryParse(
                value.AsSpan(slash + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefixLength))
        {
            return false;
        }

        var maximumPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128;
        if (prefixLength > maximumPrefix)
        {
            return false;
        }

        normalized = $"{ApplyNetworkPrefix(address, prefixLength)}/{prefixLength}";
        isUnrestricted = prefixLength == 0;
        return true;
    }

    private static IPAddress ApplyNetworkPrefix(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            bytes[wholeBytes] &= (byte)(0xff << (8 - remainingBits));
            wholeBytes++;
        }

        for (var index = wholeBytes; index < bytes.Length; index++)
        {
            bytes[index] = 0;
        }

        return new IPAddress(bytes);
    }

    private static string CreateFirewallOwnerId(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(
                property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateIdentityMapKeys(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var (mapName, itemName) in new[]
                 {
                     (MapName: "agents", ItemName: "agent"),
                     (MapName: "accounts", ItemName: "account")
                 })
        {
            var maps = document.RootElement.EnumerateObject()
                .Where(property => string.Equals(property.Name, mapName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (maps.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Duplicate config property \"{mapName}\"; property names are case-insensitive.");
            }

            if (maps.Length == 0 || maps[0].Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in maps[0].Value.EnumerateObject())
            {
                if (!ids.Add(item.Name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate {itemName} ID \"{item.Name}\"; IDs are case-insensitive.");
                }
            }
        }
    }

    private static Dictionary<string, TValue> NormalizeDictionary<TValue>(
        Dictionary<string, TValue>? source,
        string itemName)
    {
        if (source is null)
        {
            throw new InvalidOperationException($"{itemName}s must be a JSON object.");
        }

        var normalized = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || !string.Equals(key, key.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Every {itemName} ID must be non-empty and cannot start or end with whitespace.");
            }

            if (value is null)
            {
                throw new InvalidOperationException($"{itemName} \"{key}\" must be a JSON object.");
            }

            if (!normalized.TryAdd(key, value))
            {
                throw new InvalidOperationException(
                    $"Duplicate {itemName} ID \"{key}\"; IDs are case-insensitive.");
            }
        }

        return normalized;
    }
}
