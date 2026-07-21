using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace D2RHost;

public enum HostFirewallDirection
{
    Inbound = 1,
    Outbound = 2
}

public sealed record HostFirewallRuleSpec(
    string Name,
    string DisplayName,
    string Description,
    string Group,
    HostFirewallDirection Direction,
    string ProgramPath,
    int? LocalPort,
    int? RemotePort,
    string LocalAddresses,
    string RemoteAddresses);

public sealed record HostFirewallRuleState(
    string Name,
    string DisplayName,
    string Description,
    string Group,
    HostFirewallDirection Direction,
    string ProgramPath,
    int Protocol,
    string LocalPorts,
    string RemotePorts,
    string LocalAddresses,
    string RemoteAddresses,
    int Profiles,
    bool Enabled,
    bool Allow,
    bool EdgeTraversal);

public sealed record HostFirewallPolicyState(
    bool Ok,
    int ModifyState,
    int ActiveProfiles,
    string[] DisabledProfiles,
    string Message);

public sealed record HostFirewallStatus(
    bool Managed,
    bool Supported,
    bool Ok,
    bool Changed,
    DateTimeOffset CheckedAt,
    string Message,
    string[] RuleNames,
    HostFirewallPolicyState? Policy = null);

public interface IHostFirewallBackend
{
    bool IsSupported { get; }

    HostFirewallPolicyState GetPolicyState();

    IReadOnlyList<HostFirewallRuleState> ListRules();

    void Upsert(HostFirewallRuleSpec rule);

    void Remove(string name);
}

public interface IHostNetworkAddressProvider
{
    IReadOnlyList<string> GetLocalAddresses();
}

public static class HostFirewallRules
{
    public const string ManagedGroupPrefix = "D2ROps Managed Host";
    public const string LegacyManagedGroup = ManagedGroupPrefix;
    public const string LegacyHostRulePrefix = "D2ROps Host inbound TCP";
    public const int TcpProtocol = 6;
    public const int AllProfiles = int.MaxValue;

    public static string ManagedGroup(string ownerId) =>
        $"{ManagedGroupPrefix} {NormalizeOwnerId(ownerId)}";

    public static string ListenerRuleName(string ownerId) =>
        $"D2ROps.Host.{NormalizeOwnerId(ownerId)}.Listener.In";

    public static string MasterRuleName(string ownerId) =>
        $"D2ROps.Host.{NormalizeOwnerId(ownerId)}.Master.Out";

    public static HostFirewallRuleSpec[] BuildDesired(
        HostConfig config,
        string programPath,
        IEnumerable<string>? localAddresses = null)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            throw new InvalidOperationException("Could not resolve the running D2RHost executable path.");
        }

        var legacyNetworkScope = !config.WindowsFirewall.WasExplicitlyConfigured;
        var trustedNetworks = legacyNetworkScope
            ? "*"
            : NormalizeAddressList(config.WindowsFirewall.TrustedNetworks ?? ["LocalSubnet"]);
        var listenerAddresses = legacyNetworkScope
            ? "*"
            : NormalizeAddressList(localAddresses ?? ["LocalSubnet"]);
        if (string.IsNullOrWhiteSpace(listenerAddresses))
        {
            listenerAddresses = "LocalSubnet";
        }

        var ownerId = config.WindowsFirewall.OwnerId;
        var managedGroup = ManagedGroup(ownerId);
        var listenerRuleName = ListenerRuleName(ownerId);
        var rules = new List<HostFirewallRuleSpec>
        {
            new(
                Name: listenerRuleName,
                DisplayName: listenerRuleName,
                Description: "Managed D2RHost HTTP/WebSocket listener.",
                Group: managedGroup,
                Direction: HostFirewallDirection.Inbound,
                ProgramPath: Path.GetFullPath(programPath),
                LocalPort: config.HttpPort,
                RemotePort: null,
                LocalAddresses: listenerAddresses,
                RemoteAddresses: trustedNetworks)
        };

        if (config.IsWorker)
        {
            var masterUri = new Uri(config.MasterUrl!, UriKind.Absolute);
            var remotePort = masterUri.IsDefaultPort
                ? string.Equals(masterUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80
                : masterUri.Port;
            var remoteAddresses = IPAddress.TryParse(masterUri.Host, out var masterAddress)
                ? masterAddress.ToString()
                : trustedNetworks;

            var masterRuleName = MasterRuleName(ownerId);
            rules.Add(new HostFirewallRuleSpec(
                Name: masterRuleName,
                DisplayName: masterRuleName,
                Description: $"Managed D2RHost worker connection to {masterUri.Host}:{remotePort}.",
                Group: managedGroup,
                Direction: HostFirewallDirection.Outbound,
                ProgramPath: Path.GetFullPath(programPath),
                LocalPort: null,
                RemotePort: remotePort,
                LocalAddresses: listenerAddresses,
                RemoteAddresses: remoteAddresses));
        }

        return rules.ToArray();
    }

    public static bool Matches(HostFirewallRuleState current, HostFirewallRuleSpec desired)
    {
        return string.Equals(current.Name, desired.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.DisplayName, desired.DisplayName, StringComparison.Ordinal)
            && string.Equals(current.Description, desired.Description, StringComparison.Ordinal)
            && string.Equals(current.Group, desired.Group, StringComparison.Ordinal)
            && current.Direction == desired.Direction
            && PathsEqual(current.ProgramPath, desired.ProgramPath)
            && current.Protocol == TcpProtocol
            && PortsEqual(current.LocalPorts, desired.LocalPort)
            && PortsEqual(current.RemotePorts, desired.RemotePort)
            && AddressListsEqual(current.LocalAddresses, desired.LocalAddresses)
            && AddressListsEqual(current.RemoteAddresses, desired.RemoteAddresses)
            && current.Profiles == AllProfiles
            && current.Enabled
            && current.Allow
            && !current.EdgeTraversal;
    }

    public static bool IsOwnedOrLegacy(
        HostFirewallRuleState rule,
        string ownerId,
        string programPath,
        int listenerPort)
    {
        if (string.Equals(rule.Group, ManagedGroup(ownerId), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!PathsEqual(rule.ProgramPath, programPath)
            || rule.Direction != HostFirewallDirection.Inbound
            || rule.Protocol != TcpProtocol
            || !PortsEqual(rule.LocalPorts, listenerPort))
        {
            return false;
        }

        return string.Equals(rule.Group, LegacyManagedGroup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Name, LegacyHostRulePrefix, StringComparison.OrdinalIgnoreCase)
            || rule.Name.StartsWith(LegacyHostRulePrefix + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAddressList(IEnumerable<string> addresses)
    {
        return string.Join(",", addresses
            .Select(address => address.Trim())
            .Where(address => address.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, StringComparer.OrdinalIgnoreCase));
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim()),
                Path.GetFullPath(right.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool PortsEqual(string current, int? desired)
    {
        var normalized = string.IsNullOrWhiteSpace(current) ? "*" : current.Trim();
        return desired is null
            ? normalized is "*" or "Any"
            : string.Equals(normalized, desired.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddressListsEqual(string current, string desired)
    {
        var currentValues = SplitAddressList(current);
        var desiredValues = SplitAddressList(desired);
        return currentValues.SetEquals(desiredValues);
    }

    private static HashSet<string> SplitAddressList(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "*" : value;
        return normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(address => string.Equals(address, "Any", StringComparison.OrdinalIgnoreCase) ? "*" : address)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeOwnerId(string ownerId)
    {
        var normalized = new string((ownerId ?? "")
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .ToArray());
        return normalized.Length == 0 ? "default" : normalized.ToLowerInvariant();
    }
}

public sealed class HostFirewallManager : BackgroundService
{
    private static readonly TimeSpan StaleRuleRetirementGrace = TimeSpan.FromSeconds(5);
    private readonly HostConfig _config;
    private readonly IHostFirewallBackend _backend;
    private readonly IHostNetworkAddressProvider _networkAddresses;
    private readonly ILogger<HostFirewallManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _reconcileLock = new();
    private readonly SemaphoreSlim _networkChanged = new(0, 1);
    private HostFirewallStatus _status = new(
        Managed: true,
        Supported: false,
        Ok: true,
        Changed: false,
        CheckedAt: DateTimeOffset.MinValue,
        Message: "Windows firewall has not been checked yet.",
        RuleNames: []);
    private int _hasReconciled;
    private int _networkChangeSubscribed;
    private DateTimeOffset? _staleCleanupNotBeforeUtc;

    public HostFirewallManager(
        HostConfig config,
        IHostFirewallBackend backend,
        ILogger<HostFirewallManager> logger)
        : this(config, backend, new SystemHostNetworkAddressProvider(), logger)
    {
    }

    public HostFirewallManager(
        HostConfig config,
        IHostFirewallBackend backend,
        IHostNetworkAddressProvider networkAddresses,
        ILogger<HostFirewallManager> logger,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _backend = backend;
        _networkAddresses = networkAddresses;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Program.cs resolves this singleton before its startup reconciliation. Registering
        // here closes the small DHCP/address-change gap before ExecuteAsync begins.
        if (OperatingSystem.IsWindows()
            && _config.WindowsFirewall.Manage
            && _backend.IsSupported)
        {
            SubscribeToNetworkChanges();
        }
    }

    public HostFirewallStatus Status => Volatile.Read(ref _status);

    public HostFirewallStatus ReconcileNow()
    {
        lock (_reconcileLock)
        {
            Interlocked.Exchange(ref _hasReconciled, 1);
            var checkedAt = _timeProvider.GetUtcNow();
            if (!_config.WindowsFirewall.Manage)
            {
                return SetStatus(new HostFirewallStatus(
                    Managed: false,
                    Supported: _backend.IsSupported,
                    Ok: true,
                    Changed: false,
                    CheckedAt: checkedAt,
                    Message: "Windows firewall management is disabled by configuration.",
                    RuleNames: []));
            }

            if (!_backend.IsSupported)
            {
                return SetStatus(new HostFirewallStatus(
                    Managed: true,
                    Supported: false,
                    Ok: true,
                    Changed: false,
                    CheckedAt: checkedAt,
                    Message: "Windows firewall management is not applicable on this operating system.",
                    RuleNames: []));
            }

            HostFirewallRuleSpec[] desired = [];
            HostFirewallPolicyState? policy = null;
            var changed = false;
            try
            {
                var programPath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
                desired = HostFirewallRules.BuildDesired(
                    _config,
                    programPath,
                    _networkAddresses.GetLocalAddresses());

                policy = _backend.GetPolicyState();
                if (!policy.Ok)
                {
                    return SetStatus(new HostFirewallStatus(
                        Managed: true,
                        Supported: true,
                        Ok: false,
                        Changed: false,
                        CheckedAt: checkedAt,
                        Message: policy.Message,
                        RuleNames: desired.Select(rule => rule.Name).ToArray(),
                        Policy: policy));
                }

                var current = _backend.ListRules();
                var repaired = false;
                foreach (var rule in desired)
                {
                    var existing = current.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is null || !HostFirewallRules.Matches(existing, rule))
                    {
                        _backend.Upsert(rule);
                        changed = true;
                        repaired = true;
                    }
                }

                if (repaired)
                {
                    _staleCleanupNotBeforeUtc = checkedAt + StaleRuleRetirementGrace;
                }

                // Verify every desired field before retiring a rule. If creation or repair was
                // blocked by permissions/GPO, the last-known rule remains available for recovery.
                var verified = _backend.ListRules();
                foreach (var rule in desired)
                {
                    var actual = verified.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
                    if (actual is null || !HostFirewallRules.Matches(actual, rule))
                    {
                        throw new InvalidOperationException(
                            $"Windows Firewall did not retain the desired state for {rule.DisplayName}.");
                    }
                }

                policy = _backend.GetPolicyState();
                if (!policy.Ok)
                {
                    return SetStatus(new HostFirewallStatus(
                        Managed: true,
                        Supported: true,
                        Ok: false,
                        Changed: changed,
                        CheckedAt: checkedAt,
                        Message: policy.Message,
                        RuleNames: desired.Select(rule => rule.Name).ToArray(),
                        Policy: policy));
                }

                var desiredNames = desired.Select(rule => rule.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var cleanupDeferred = repaired
                    || (_staleCleanupNotBeforeUtc is { } notBefore && checkedAt < notBefore);
                if (!cleanupDeferred)
                {
                    foreach (var stale in verified.Where(rule => HostFirewallRules.IsOwnedOrLegacy(
                                 rule,
                                 _config.WindowsFirewall.OwnerId,
                                 programPath,
                                 _config.HttpPort)))
                    {
                        if (desiredNames.Contains(stale.Name))
                        {
                            continue;
                        }

                        _backend.Remove(stale.Name);
                        changed = true;
                    }

                    _staleCleanupNotBeforeUtc = null;
                }

                var message = cleanupDeferred
                    ? "Windows firewall desired state is verified; stale rules remain until the stable follow-up check completes."
                    : changed
                        ? "Windows firewall desired state was reconciled; stale rules owned by this D2RHost installation were removed where present."
                        : "Windows firewall rules and effective policy match the desired node topology.";
                var result = new HostFirewallStatus(
                    Managed: true,
                    Supported: true,
                    Ok: true,
                    Changed: changed,
                    CheckedAt: checkedAt,
                    Message: message,
                    RuleNames: desired.Select(rule => rule.Name).ToArray(),
                    Policy: policy);
                if (changed)
                {
                    _logger.LogInformation("{Message}", message);
                }

                return SetStatus(result);
            }
            catch (Exception ex)
            {
                var message = "Windows firewall reconciliation failed. Run D2RHost elevated or apply equivalent rules through Group Policy: "
                    + ex.Message;
                _logger.LogWarning(ex, "{Message}", message);
                return SetStatus(new HostFirewallStatus(
                    Managed: true,
                    Supported: true,
                    Ok: false,
                    Changed: changed,
                    CheckedAt: checkedAt,
                    Message: message,
                    RuleNames: desired.Select(rule => rule.Name).ToArray(),
                    Policy: policy));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.WindowsFirewall.Manage || !_backend.IsSupported)
        {
            return;
        }

        SubscribeToNetworkChanges();
        try
        {
            if (Volatile.Read(ref _hasReconciled) == 0)
            {
                ReconcileNow();
            }

            var interval = TimeSpan.FromSeconds(_config.WindowsFirewall.ReconcileSeconds);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _networkChanged.WaitAsync(interval, stoppingToken);
                ReconcileNow();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
        finally
        {
            UnsubscribeFromNetworkChanges();
        }
    }

    private void HandleNetworkAddressChanged(object? sender, EventArgs args)
    {
        try
        {
            _networkChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending reconciliation is enough to absorb a burst of adapter events.
        }
    }

    private HostFirewallStatus SetStatus(HostFirewallStatus status)
    {
        Volatile.Write(ref _status, status);
        return status;
    }

    private void SubscribeToNetworkChanges()
    {
        if (Interlocked.CompareExchange(ref _networkChangeSubscribed, 1, 0) == 0)
        {
            NetworkChange.NetworkAddressChanged += HandleNetworkAddressChanged;
        }
    }

    private void UnsubscribeFromNetworkChanges()
    {
        if (Interlocked.Exchange(ref _networkChangeSubscribed, 0) == 1)
        {
            NetworkChange.NetworkAddressChanged -= HandleNetworkAddressChanged;
        }
    }

    public override void Dispose()
    {
        UnsubscribeFromNetworkChanges();
        base.Dispose();
    }
}

public sealed class SystemHostNetworkAddressProvider : IHostNetworkAddressProvider
{
    public IReadOnlyList<string> GetLocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                    or System.Net.Sockets.AddressFamily.InterNetworkV6)
                .Where(address => !IPAddress.IsLoopback(address)
                    && !address.Equals(IPAddress.Any)
                    && !address.Equals(IPAddress.IPv6Any)
                    && !address.IsIPv6LinkLocal)
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // LocalSubnet remains dynamic inside Windows Firewall. It is a safe bootstrap
            // while DHCP/NLA is still assigning the first usable interface address.
            return addresses.Length == 0 ? ["LocalSubnet"] : addresses;
        }
        catch (NetworkInformationException)
        {
            return ["LocalSubnet"];
        }
    }
}

public sealed class WindowsComHostFirewallBackend : IHostFirewallBackend
{
    private const int NetFwActionAllow = 1;
    private const int NetFwModifyStateOk = 0;
    private const int NetFwModifyStateGroupPolicyOverride = 1;
    private const int NetFwModifyStateInboundBlocked = 2;
    private static readonly (int Value, string Name)[] ProfileTypes =
    [
        (1, "Domain"),
        (2, "Private"),
        (4, "Public")
    ];

    public bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public HostFirewallPolicyState GetPolicyState()
    {
        EnsureWindows();
        return GetPolicyStateWindows();
    }

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<HostFirewallRuleState> ListRules()
    {
        EnsureWindows();
        return ListRulesWindows();
    }

    [SupportedOSPlatform("windows")]
    public void Upsert(HostFirewallRuleSpec rule)
    {
        EnsureWindows();
        UpsertWindows(rule);
    }

    [SupportedOSPlatform("windows")]
    public void Remove(string name)
    {
        EnsureWindows();
        RemoveWindows(name);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<HostFirewallRuleState> ListRulesWindows()
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            var states = new List<HostFirewallRuleState>();
            foreach (var item in (IEnumerable)rulesObject)
            {
                try
                {
                    dynamic rule = item;
                    states.Add(new HostFirewallRuleState(
                        Name: ReadString(() => rule.Name),
                        DisplayName: ReadString(() => rule.Name),
                        Description: ReadString(() => rule.Description),
                        Group: ReadString(() => rule.Grouping),
                        Direction: (HostFirewallDirection)ReadInt(() => rule.Direction),
                        ProgramPath: ReadString(() => rule.ApplicationName),
                        Protocol: ReadInt(() => rule.Protocol),
                        LocalPorts: ReadString(() => rule.LocalPorts, "*"),
                        RemotePorts: ReadString(() => rule.RemotePorts, "*"),
                        LocalAddresses: ReadString(() => rule.LocalAddresses, "*"),
                        RemoteAddresses: ReadString(() => rule.RemoteAddresses, "*"),
                        Profiles: ReadInt(() => rule.Profiles),
                        Enabled: ReadBool(() => rule.Enabled),
                        Allow: ReadInt(() => rule.Action) == NetFwActionAllow,
                        EdgeTraversal: ReadBool(() => rule.EdgeTraversal)));
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return states;
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static HostFirewallPolicyState GetPolicyStateWindows()
    {
        object? policyObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            var modifyState = Convert.ToInt32(policy.LocalPolicyModifyState);
            var activeProfiles = Convert.ToInt32(policy.CurrentProfileTypes);
            var activeProfileNames = new List<string>();
            var disabledProfiles = new List<string>();
            foreach (var profile in ProfileTypes)
            {
                if ((activeProfiles & profile.Value) == 0)
                {
                    continue;
                }

                activeProfileNames.Add(profile.Name);
                if (!Convert.ToBoolean(policy.FirewallEnabled[profile.Value]))
                {
                    disabledProfiles.Add(profile.Name);
                }
            }

            if (modifyState != NetFwModifyStateOk)
            {
                var message = modifyState switch
                {
                    NetFwModifyStateGroupPolicyOverride =>
                        "Windows Firewall Group Policy overrides local rules, so D2RHost cannot guarantee that its node rules take effect. Allow local policy merge or set windowsFirewall.manage=false and deploy equivalent rules centrally.",
                    NetFwModifyStateInboundBlocked =>
                        "Windows Firewall policy blocks unsolicited inbound traffic, so the D2RHost listener rule cannot take effect. Change the central policy or set windowsFirewall.manage=false and deploy an equivalent exception centrally.",
                    _ =>
                        $"Windows Firewall reports unknown local policy state {modifyState}; D2RHost cannot verify that its rules take effect."
                };
                return new HostFirewallPolicyState(
                    Ok: false,
                    ModifyState: modifyState,
                    ActiveProfiles: activeProfiles,
                    DisabledProfiles: disabledProfiles.ToArray(),
                    Message: message);
            }

            if (activeProfileNames.Count == 0)
            {
                return new HostFirewallPolicyState(
                    Ok: false,
                    ModifyState: modifyState,
                    ActiveProfiles: activeProfiles,
                    DisabledProfiles: Array.Empty<string>(),
                    Message: "Windows Firewall reported no recognized active network profile; D2RHost cannot verify enforcement.");
            }

            if (disabledProfiles.Count > 0)
            {
                return new HostFirewallPolicyState(
                    Ok: false,
                    ModifyState: modifyState,
                    ActiveProfiles: activeProfiles,
                    DisabledProfiles: disabledProfiles.ToArray(),
                    Message: $"Windows Firewall is disabled for active profile(s): {string.Join(", ", disabledProfiles)}. D2RHost rules are stored but are not enforced.");
            }

            return new HostFirewallPolicyState(
                Ok: true,
                ModifyState: modifyState,
                ActiveProfiles: activeProfiles,
                DisabledProfiles: Array.Empty<string>(),
                Message: $"Windows Firewall local rules are effective on active profile(s): {string.Join(", ", activeProfileNames)}.");
        }
        finally
        {
            ReleaseComObject(policyObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UpsertWindows(HostFirewallRuleSpec desired)
    {
        object? policyObject = null;
        object? rulesObject = null;
        object? detachedRuleObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            detachedRuleObject = CreateComObject("HNetCfg.FWRule");
            dynamic rule = detachedRuleObject;
            rule.Name = desired.Name;
            rule.Description = desired.Description;
            rule.Grouping = desired.Group;
            rule.ApplicationName = desired.ProgramPath;
            rule.Protocol = HostFirewallRules.TcpProtocol;
            rule.LocalPorts = desired.LocalPort?.ToString() ?? "*";
            rule.RemotePorts = desired.RemotePort?.ToString() ?? "*";
            rule.LocalAddresses = desired.LocalAddresses;
            rule.RemoteAddresses = desired.RemoteAddresses;
            rule.Direction = (int)desired.Direction;
            rule.Profiles = HostFirewallRules.AllProfiles;
            rule.EdgeTraversal = false;
            rule.Action = NetFwActionAllow;
            rule.Enabled = true;

            // INetFwRules.Add replaces a rule with the same identifier. Building the
            // object while detached keeps the installed recovery rule intact if any
            // property validation or COM call fails before the collection replacement.
            dynamic rules = rulesObject;
            rules.Add(rule);
        }
        finally
        {
            ReleaseComObject(detachedRuleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindows(string name)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            policyObject = CreateComObject("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            rules.Remove(name);
        }
        finally
        {
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object CreateComObject(string programmaticId)
    {
        var type = Type.GetTypeFromProgID(programmaticId)
            ?? throw new InvalidOperationException($"Windows Firewall COM class is unavailable: {programmaticId}");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create Windows Firewall COM class: {programmaticId}");
    }

    private static string ReadString(Func<object?> read, string fallback = "")
    {
        try
        {
            return Convert.ToString(read()) ?? fallback;
        }
        catch (COMException)
        {
            return fallback;
        }
    }

    private static int ReadInt(Func<object?> read)
    {
        try
        {
            return Convert.ToInt32(read());
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static bool ReadBool(Func<object?> read)
    {
        try
        {
            return Convert.ToBoolean(read());
        }
        catch (COMException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Firewall management is only available on Windows.");
        }
    }
}
