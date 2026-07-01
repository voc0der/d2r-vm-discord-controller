namespace D2RHost;

public sealed class HostConfig
{
    public string DiscordToken { get; set; } = "";
    public ulong? DiscordGuildId { get; set; }
    public bool DisableDiscord { get; set; }
    public int HttpPort { get; set; } = 8080;
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
