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
    // Account whose Settings.json is copied onto a VM whose own file D2R has reset (the first-run
    // gamma calibration screen). Null picks the first connected, uncorrupted account by key. This
    // only sets the order candidates are tried in - a configured donor that is offline or itself
    // broken is skipped rather than blocking the repair.
    public string? SettingsDonorAccountKey { get; set; }
    public VmHangRecoveryConfig VmHangRecovery { get; set; } = new();
    public StuckVmWatchdogConfig StuckVmWatchdog { get; set; } = new();
    public Dictionary<string, HostAgentConfig> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AccountConfig> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Recovery for a VM that freezes partway through a restart - typically sitting on the Windows boot
/// logo, never reaching a desktop, with its agent unable to report anything because it never
/// started. A graceful stop cannot clear that (it is routed through guest integration services the
/// frozen guest is not running), so the host cuts power outright and starts the VM again.
///
/// Every default here is deliberately patient. This only ever runs where the host has already given
/// up waiting and the alternative is restarting the whole physical node, so the cost of waiting a
/// little longer is small and the cost of cutting a healthy guest's power is not.
/// See VmHangRecoveryPolicy.
/// </summary>
public sealed class VmHangRecoveryConfig
{
    /// <summary>
    /// Set false to keep the pre-existing behavior: a VM that will not come back is reported as a
    /// failed recovery and escalates to a node restart without a power cut being attempted.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a powered-on guest may go without a Hyper-V heartbeat before it counts as wedged.
    /// Only applies when the heartbeat is actually being reported and says there is no contact -
    /// positive evidence the guest never reached a working Windows. Must clear a genuine cold boot
    /// with room to spare; raise it before lowering it.
    /// </summary>
    public int HangSuspectedAfterSeconds { get; set; } = 300;

    /// <summary>
    /// The fallback window when the heartbeat is NOT reported (integration service disabled or
    /// absent, or a worker node too old to send it). With no evidence to act on, a power cut waits
    /// out this much longer window instead of the one above. Defaults to the full agent-reconnect
    /// budget, so it costs nothing that was not already being spent.
    /// </summary>
    public int NoEvidenceGraceSeconds { get; set; } = 1200;

    /// <summary>
    /// How long to leave the VM off before starting it again. Hyper-V releases the guest's devices
    /// and memory asynchronously, and starting into that teardown is its own source of a wedged
    /// boot - the exact failure this is trying to clear.
    /// </summary>
    public int SettleSeconds { get; set; } = 10;

    /// <summary>
    /// Hard power cuts allowed per recovery attempt. A guest that will not come back after this
    /// many is a host or hardware problem, and looping would only delay the node-level escalation
    /// that can actually help.
    /// </summary>
    public int MaxHardPowerCuts { get; set; } = 2;
}

/// <summary>
/// The standing sweep that notices a VM whose agent has stopped answering while the VM itself is
/// still powered on, and power-cycles it without waiting for follow-auto to work through its own
/// ladders. See StuckVmWatchdogPolicy for why an offline agent - and not Hyper-V's power state -
/// is the signal this keys on.
/// </summary>
public sealed class StuckVmWatchdogConfig
{
    /// <summary>
    /// Set false to keep the pre-existing behavior: a stuck guest is noticed only when follow-auto
    /// runs out of patience with it, or when an operator notices it offline.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long an agent may be continuously missing, on a VM Hyper-V still reports powered on,
    /// before its guest is power-cycled. Must comfortably clear a cold boot plus the agent's own
    /// connect: below that this recovers VMs that were about to arrive on their own.
    /// </summary>
    public int AgentOfflineGraceSeconds { get; set; } = 240;

    /// <summary>
    /// The longer window used when the Heartbeat integration service reports nothing for this VM,
    /// so the offline streak has no corroboration at all. Enabling Heartbeat on the VM is what buys
    /// the shorter window above.
    /// </summary>
    public int NoHeartbeatEvidenceGraceSeconds { get; set; } = 600;

    /// <summary>
    /// How long a VM must have been powered on before an absent agent means anything. This is the
    /// guard against acting on guests that are legitimately still booting - after a host resume,
    /// every VM is doing exactly that at once.
    /// </summary>
    public int MinimumVmUptimeSeconds { get; set; } = 180;

    /// <summary>
    /// Power cycles allowed per guest before the watchdog reports the dead end and stops. Refunded
    /// once that guest's agent has stayed connected for <see cref="AgentOfflineGraceSeconds"/> - a
    /// brief reappearance does not, or a guest whose agent connects and dies again would reclaim
    /// its whole allowance every few minutes and never reach the dead-end notice.
    /// </summary>
    public int MaxRecoveriesPerVm { get; set; } = 2;

    /// <summary>How often the sweep runs.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;
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
