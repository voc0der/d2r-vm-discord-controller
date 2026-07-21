using System.Text.Json.Serialization;

namespace D2RHost;

public sealed class HostConfig
{
    public const string MasterMode = "master";
    public const string WorkerMode = "worker";

    public string Mode { get; set; } = MasterMode;
    public string NodeId { get; set; } = "local";
    public string? MasterUrl { get; set; }
    public string? MasterSharedSecret { get; set; }
    public int NodeHeartbeatSeconds { get; set; } = 15;
    public int AgentOfflineAfterSeconds { get; set; } = 45;

    [JsonIgnore]
    public bool IsMaster => string.Equals(Mode, MasterMode, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsWorker => string.Equals(Mode, WorkerMode, StringComparison.OrdinalIgnoreCase);

    public string DiscordToken { get; set; } = "";
    public ulong? DiscordGuildId { get; set; }
    public bool DisableDiscord { get; set; }
    public int HttpPort { get; set; } = 8080;
    public WindowsFirewallConfig WindowsFirewall { get; set; } = new();
    public string DatabasePath { get; set; } = @"C:\D2ROps\d2r-host.sqlite";
    public string[] AllowedDiscordUserIds { get; set; } = [];
    public int StartAllDelaySeconds { get; set; } = 20;
    public int? ClientStaggerSeconds { get; set; }
    public bool GameSessionNotificationsEnabled { get; set; }
    public bool UpdateNotificationsEnabled { get; set; } = true;
    public ulong? GuildChannel { get; set; }
    public string PowerShellPath { get; set; } = "powershell.exe";
    public int PowerShellTimeoutSeconds { get; set; } = 90;
    public string[] AllowedVmNamePrefixes { get; set; } = [];
    public Dictionary<string, HostAgentConfig> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AccountConfig> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WindowsFirewallConfig
{
    public bool Manage { get; set; } = true;
    public string[] TrustedNetworks { get; set; } = ["LocalSubnet"];
    public int ReconcileSeconds { get; set; } = 30;

    [JsonIgnore]
    public bool WasExplicitlyConfigured { get; set; } = true;

    [JsonIgnore]
    public string OwnerId { get; set; } = "default";
}

public sealed class HostAgentConfig
{
    public string Kind { get; set; } = "vm";
    public string? DisplayName { get; set; }
    public string SharedSecret { get; set; } = "";
    public string? RemoteUrl { get; set; }
}

public sealed class AccountConfig
{
    public string AgentId { get; set; } = "";
    public string? NodeId { get; set; }
    public string? DisplayName { get; set; }
    public string? VmName { get; set; }
    public int? CharacterSlot { get; set; }
}

public sealed record ActiveGame(
    string Name,
    string? Password,
    string? Difficulty,
    string? Notes,
    string UpdatedBy,
    DateTimeOffset UpdatedUtc);
