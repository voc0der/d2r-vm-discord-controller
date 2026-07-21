using System.Diagnostics;
using D2RHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace D2RAgent.Tests;

public sealed class HostFirewallManagerTests
{
    private const string TestOwnerId = "test-owner";
    private static readonly string[] TestLocalAddresses = ["10.2.39.65"];
    private static readonly HostFirewallPolicyState EffectivePrivatePolicy = new(
        Ok: true,
        ModifyState: 0,
        ActiveProfiles: 2,
        DisabledProfiles: [],
        Message: "Windows Firewall local rules are effective on active profile(s): Private.");

    [Fact]
    public void MasterDesiredRulesContainOnlyTheScopedInboundListener()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        config.NodeId = "server-a";
        config.HttpPort = 8123;
        config.WindowsFirewall.TrustedNetworks =
        [
            " LocalSubnet ",
            "10.20.0.0/16",
            "localsubnet",
            ""
        ];
        var programPath = Path.Combine(".", "publish", "D2RHost.exe");

        var rule = Assert.Single(HostFirewallRules.BuildDesired(
            config,
            programPath,
            TestLocalAddresses));

        Assert.Equal(HostFirewallRules.ListenerRuleName(TestOwnerId), rule.Name);
        Assert.Equal(HostFirewallRules.ListenerRuleName(TestOwnerId), rule.DisplayName);
        Assert.Equal("Managed D2RHost HTTP/WebSocket listener.", rule.Description);
        Assert.Equal(HostFirewallRules.ManagedGroup(TestOwnerId), rule.Group);
        Assert.Equal(HostFirewallDirection.Inbound, rule.Direction);
        Assert.Equal(Path.GetFullPath(programPath), rule.ProgramPath);
        Assert.Equal(8123, rule.LocalPort);
        Assert.Null(rule.RemotePort);
        Assert.Equal("10.2.39.65", rule.LocalAddresses);
        Assert.Equal("10.20.0.0/16,LocalSubnet", rule.RemoteAddresses);
    }

    [Theory]
    [InlineData("ws://192.0.2.10/node", 80, "192.0.2.10")]
    [InlineData("wss://192.0.2.10/node", 443, "192.0.2.10")]
    [InlineData("ws://192.0.2.10:8080/node", 8080, "192.0.2.10")]
    [InlineData("wss://[2001:db8::5]/node", 443, "2001:db8::5")]
    [InlineData("ws://master.example/node", 80, "10.20.0.0/16,LocalSubnet")]
    [InlineData("wss://master.example:8443/node", 8443, "10.20.0.0/16,LocalSubnet")]
    public void WorkerDesiredRulesScopeTheMasterTargetAndSchemePort(
        string masterUrl,
        int expectedPort,
        string expectedRemoteAddresses)
    {
        var config = CreateConfig(HostConfig.WorkerMode);
        config.NodeId = "server-b";
        config.MasterUrl = masterUrl;

        var rules = HostFirewallRules.BuildDesired(
            config,
            Path.Combine("publish", "D2RHost.exe"),
            TestLocalAddresses);

        Assert.Equal(2, rules.Length);
        Assert.Contains(rules, rule => rule.Name == HostFirewallRules.ListenerRuleName(TestOwnerId));
        var masterRule = Assert.Single(
            rules,
            rule => rule.Name == HostFirewallRules.MasterRuleName(TestOwnerId));
        Assert.Equal(HostFirewallDirection.Outbound, masterRule.Direction);
        Assert.Null(masterRule.LocalPort);
        Assert.Equal(expectedPort, masterRule.RemotePort);
        Assert.Equal("10.2.39.65", masterRule.LocalAddresses);
        Assert.Equal(expectedRemoteAddresses, masterRule.RemoteAddresses);
        Assert.Contains($"to {new Uri(masterUrl).Host}:{expectedPort}", masterRule.Description);
    }

    [Fact]
    public void LegacyOmittedFirewallConfigPreservesAnyAddressScopes()
    {
        var config = CreateConfig(HostConfig.WorkerMode);
        config.MasterUrl = "ws://master.example/node";
        config.WindowsFirewall.WasExplicitlyConfigured = false;

        var rules = HostFirewallRules.BuildDesired(
            config,
            Path.Combine("publish", "D2RHost.exe"),
            ["10.2.39.65", "2001:db8::65"]);

        var listener = Assert.Single(
            rules,
            rule => rule.Name == HostFirewallRules.ListenerRuleName(TestOwnerId));
        Assert.Equal("*", listener.LocalAddresses);
        Assert.Equal("*", listener.RemoteAddresses);

        var master = Assert.Single(
            rules,
            rule => rule.Name == HostFirewallRules.MasterRuleName(TestOwnerId));
        Assert.Equal("*", master.LocalAddresses);
        Assert.Equal("*", master.RemoteAddresses);
        Assert.Equal(80, master.RemotePort);
    }

    [Fact]
    public void DesiredRulesIncludeIpv6LocalAddresses()
    {
        var config = CreateConfig(HostConfig.MasterMode);

        var rule = Assert.Single(HostFirewallRules.BuildDesired(
            config,
            Path.Combine("publish", "D2RHost.exe"),
            ["2001:db8::65", "10.2.39.65"]));

        Assert.Equal("10.2.39.65,2001:db8::65", rule.LocalAddresses);
    }

    [Fact]
    public void OwnerSpecificRuleNamesAndGroupsDoNotCollide()
    {
        var firstConfig = CreateConfig(HostConfig.WorkerMode);
        firstConfig.WindowsFirewall.OwnerId = "install-a";
        var secondConfig = CreateConfig(HostConfig.WorkerMode);
        secondConfig.WindowsFirewall.OwnerId = "install-b";
        var programPath = Path.Combine("publish", "D2RHost.exe");

        var firstRules = HostFirewallRules.BuildDesired(firstConfig, programPath, TestLocalAddresses);
        var secondRules = HostFirewallRules.BuildDesired(secondConfig, programPath, TestLocalAddresses);

        Assert.Equal(
            [
                HostFirewallRules.ListenerRuleName("install-a"),
                HostFirewallRules.MasterRuleName("install-a")
            ],
            firstRules.Select(rule => rule.Name));
        Assert.Equal(
            [
                HostFirewallRules.ListenerRuleName("install-b"),
                HostFirewallRules.MasterRuleName("install-b")
            ],
            secondRules.Select(rule => rule.Name));
        Assert.All(firstRules, rule => Assert.Equal(HostFirewallRules.ManagedGroup("install-a"), rule.Group));
        Assert.All(secondRules, rule => Assert.Equal(HostFirewallRules.ManagedGroup("install-b"), rule.Group));
        Assert.Empty(firstRules.Select(rule => rule.Name).Intersect(
            secondRules.Select(rule => rule.Name),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactRulesRequireNoChanges()
    {
        var config = CreateConfig(HostConfig.WorkerMode);
        var backend = new FakeHostFirewallBackend(isSupported: true);
        backend.Rules.AddRange(BuildDesiredForCurrentProcess(config).Select(ToState));
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.True(status.Ok);
        Assert.False(status.Changed);
        Assert.Equal(2, backend.ListCalls);
        Assert.Equal(2, backend.PolicyCalls);
        Assert.Empty(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Equal(
            [
                HostFirewallRules.ListenerRuleName(TestOwnerId),
                HostFirewallRules.MasterRuleName(TestOwnerId)
            ],
            status.RuleNames);
    }

    [Fact]
    public void RepairedDesiredRuleRetainsStaleUntilStableFollowUpGraceExpires()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var backend = new FakeHostFirewallBackend(isSupported: true);
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var stale = ToState(desired) with
        {
            Name = "D2ROps.Host.Stale",
            DisplayName = "D2ROps.Host.Stale",
            Group = HostFirewallRules.ManagedGroup(TestOwnerId)
        };
        backend.Rules.Add(ToState(desired) with
        {
            ProgramPath = Path.Combine(Path.GetTempPath(), "old", "D2RHost.exe"),
            LocalPorts = "9999",
            Enabled = false
        });
        backend.Rules.Add(stale);
        using var manager = CreateManager(config, backend, timeProvider: timeProvider);

        var repairStatus = manager.ReconcileNow();

        Assert.True(repairStatus.Ok);
        Assert.True(repairStatus.Changed);
        var upsert = Assert.Single(backend.UpsertCalls);
        Assert.Equal(HostFirewallRules.ListenerRuleName(TestOwnerId), upsert.Name);
        Assert.Equal(2, backend.ListCalls);
        Assert.Equal(2, backend.PolicyCalls);
        Assert.Contains(backend.Rules, rule => HostFirewallRules.Matches(rule, desired));
        Assert.Contains(backend.Rules, rule => rule.Name == stale.Name);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains("stable follow-up check", repairStatus.Message);

        var immediateFollowUp = manager.ReconcileNow();

        Assert.True(immediateFollowUp.Ok);
        Assert.False(immediateFollowUp.Changed);
        Assert.Equal(4, backend.ListCalls);
        Assert.Equal(4, backend.PolicyCalls);
        Assert.Single(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains(backend.Rules, rule => rule.Name == stale.Name);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var stableFollowUp = manager.ReconcileNow();

        Assert.True(stableFollowUp.Ok);
        Assert.True(stableFollowUp.Changed);
        Assert.Equal(6, backend.ListCalls);
        Assert.Equal(6, backend.PolicyCalls);
        Assert.Single(backend.UpsertCalls);
        Assert.Equal([stale.Name], backend.RemoveCalls);
        Assert.DoesNotContain(backend.Rules, rule => rule.Name == stale.Name);
    }

    [Fact]
    public void ExactDesiredRuleRemovesOwnedAndMatchingLegacyStaleRulesOnly()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var desiredState = ToState(desired);
        var staleManaged = desiredState with
        {
            Name = "D2ROps.Host.Stale",
            DisplayName = "D2ROps.Host.Stale",
            Group = HostFirewallRules.ManagedGroup(TestOwnerId)
        };
        var legacyExact = desiredState with
        {
            Name = HostFirewallRules.LegacyHostRulePrefix,
            DisplayName = HostFirewallRules.LegacyHostRulePrefix,
            Group = ""
        };
        var legacySuffixed = desiredState with
        {
            Name = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            DisplayName = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            Group = ""
        };
        var unrelated = desiredState with
        {
            Name = "Operator.Managed.Rule",
            DisplayName = "Operator.Managed.Rule",
            Group = "Operator Rules"
        };
        var backend = new FakeHostFirewallBackend(isSupported: true);
        backend.Rules.AddRange([desiredState, staleManaged, legacyExact, legacySuffixed, unrelated]);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.True(status.Ok);
        Assert.True(status.Changed);
        Assert.Equal(2, backend.ListCalls);
        Assert.Equal(2, backend.PolicyCalls);
        Assert.Empty(backend.UpsertCalls);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                staleManaged.Name,
                legacyExact.Name,
                legacySuffixed.Name
            },
            backend.RemoveCalls.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(backend.Rules, rule => rule.Name == desired.Name);
        Assert.Contains(backend.Rules, rule => rule.Name == unrelated.Name);
    }

    [Fact]
    public void CleanupIgnoresAnotherOwnersGroupAndUnrelatedLegacyRules()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var desiredState = ToState(desired);
        var anotherOwner = desiredState with
        {
            Name = HostFirewallRules.ListenerRuleName("another-owner"),
            DisplayName = HostFirewallRules.ListenerRuleName("another-owner"),
            Group = HostFirewallRules.ManagedGroup("another-owner")
        };
        var otherProgramLegacy = desiredState with
        {
            Name = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            DisplayName = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            Group = HostFirewallRules.LegacyManagedGroup,
            ProgramPath = Path.Combine(Path.GetTempPath(), "another-install", "D2RHost.exe")
        };
        var otherPortLegacy = desiredState with
        {
            Name = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 9999",
            DisplayName = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 9999",
            Group = HostFirewallRules.LegacyManagedGroup,
            LocalPorts = "9999"
        };
        var backend = new FakeHostFirewallBackend(isSupported: true);
        backend.Rules.AddRange([desiredState, anotherOwner, otherProgramLegacy, otherPortLegacy]);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.True(status.Ok);
        Assert.False(status.Changed);
        Assert.Equal(2, backend.ListCalls);
        Assert.Equal(2, backend.PolicyCalls);
        Assert.Empty(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains(backend.Rules, rule => rule.Name == anotherOwner.Name);
        Assert.Contains(backend.Rules, rule => rule.Name == otherProgramLegacy.Name);
        Assert.Contains(backend.Rules, rule => rule.Name == otherPortLegacy.Name);
    }

    [Fact]
    public void UpsertFailureDoesNotRemoveRecoveryRules()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var stale = ToState(desired) with
        {
            Name = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            DisplayName = HostFirewallRules.LegacyHostRulePrefix + " D2RHost 8080",
            Group = ""
        };
        var backend = new FakeHostFirewallBackend(isSupported: true)
        {
            UpsertException = new InvalidOperationException("simulated upsert denial")
        };
        backend.Rules.Add(ToState(desired) with { Enabled = false });
        backend.Rules.Add(stale);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.False(status.Ok);
        Assert.False(status.Changed);
        Assert.Contains("simulated upsert denial", status.Message);
        Assert.Equal(1, backend.PolicyCalls);
        Assert.Equal(1, backend.ListCalls);
        Assert.Single(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains(backend.Rules, rule => rule.Name == stale.Name);
    }

    [Fact]
    public void VerificationFailureDoesNotRemoveRecoveryRules()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var stale = ToState(desired) with
        {
            Name = "D2ROps.Host.Stale",
            DisplayName = "D2ROps.Host.Stale",
            Group = HostFirewallRules.ManagedGroup(TestOwnerId)
        };
        var backend = new FakeHostFirewallBackend(isSupported: true)
        {
            PersistUpserts = false
        };
        backend.Rules.Add(ToState(desired) with { LocalPorts = "9999" });
        backend.Rules.Add(stale);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.False(status.Ok);
        Assert.True(status.Changed);
        Assert.Contains("did not retain the desired state", status.Message);
        Assert.Equal(1, backend.PolicyCalls);
        Assert.Equal(2, backend.ListCalls);
        Assert.Single(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains(backend.Rules, rule => rule.Name == stale.Name);
    }

    [Theory]
    [InlineData(1, false, "Group Policy prevents local Windows Firewall rules from taking effect.")]
    [InlineData(0, true, "Windows Firewall is disabled for active profile(s): Private.")]
    public void IneffectiveInitialPolicyStopsBeforeReadingOrMutatingRules(
        int modifyState,
        bool privateProfileDisabled,
        string message)
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var policy = new HostFirewallPolicyState(
            Ok: false,
            ModifyState: modifyState,
            ActiveProfiles: 2,
            DisabledProfiles: privateProfileDisabled ? ["Private"] : [],
            Message: message);
        var backend = new FakeHostFirewallBackend(isSupported: true)
        {
            PolicyState = policy
        };
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.False(status.Ok);
        Assert.False(status.Changed);
        Assert.Same(policy, status.Policy);
        Assert.Equal(message, status.Message);
        Assert.Equal(1, backend.PolicyCalls);
        Assert.Equal(0, backend.ListCalls);
        Assert.Empty(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
    }

    [Fact]
    public void PolicyTurningIneffectiveAfterUpsertRetainsStaleRecoveryRule()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        var desired = Assert.Single(BuildDesiredForCurrentProcess(config));
        var stale = ToState(desired) with
        {
            Name = "D2ROps.Host.Stale",
            DisplayName = "D2ROps.Host.Stale",
            Group = HostFirewallRules.ManagedGroup(TestOwnerId)
        };
        var ineffectivePolicy = new HostFirewallPolicyState(
            Ok: false,
            ModifyState: 1,
            ActiveProfiles: 2,
            DisabledProfiles: [],
            Message: "Group Policy began overriding local firewall rules.");
        var backend = new FakeHostFirewallBackend(isSupported: true);
        backend.PolicyStates.Enqueue(EffectivePrivatePolicy);
        backend.PolicyStates.Enqueue(ineffectivePolicy);
        backend.Rules.Add(ToState(desired) with { Enabled = false });
        backend.Rules.Add(stale);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.False(status.Ok);
        Assert.True(status.Changed);
        Assert.Same(ineffectivePolicy, status.Policy);
        Assert.Equal(2, backend.PolicyCalls);
        Assert.Equal(2, backend.ListCalls);
        Assert.Single(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
        Assert.Contains(backend.Rules, rule => rule.Name == stale.Name);
    }

    [Fact]
    public void DisabledManagementDoesNotTouchTheBackend()
    {
        var config = CreateConfig(HostConfig.MasterMode);
        config.WindowsFirewall.Manage = false;
        var backend = new FakeHostFirewallBackend(isSupported: true);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.False(status.Managed);
        Assert.True(status.Supported);
        Assert.True(status.Ok);
        Assert.False(status.Changed);
        Assert.Empty(status.RuleNames);
        AssertBackendWasNotCalled(backend);
    }

    [Fact]
    public void UnsupportedPlatformDoesNotTouchTheBackend()
    {
        var config = CreateConfig(HostConfig.WorkerMode);
        var backend = new FakeHostFirewallBackend(isSupported: false);
        using var manager = CreateManager(config, backend);

        var status = manager.ReconcileNow();

        Assert.True(status.Managed);
        Assert.False(status.Supported);
        Assert.True(status.Ok);
        Assert.False(status.Changed);
        Assert.Empty(status.RuleNames);
        AssertBackendWasNotCalled(backend);
    }

    [Fact]
    public void RuleMatchingNormalizesPathsPortsAndAddressSets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "d2rops-firewall-tests", "publish");
        var desired = new HostFirewallRuleSpec(
            Name: HostFirewallRules.ListenerRuleName(TestOwnerId),
            DisplayName: HostFirewallRules.ListenerRuleName(TestOwnerId),
            Description: "Managed listener.",
            Group: HostFirewallRules.ManagedGroup(TestOwnerId),
            Direction: HostFirewallDirection.Inbound,
            ProgramPath: Path.Combine(directory, "D2RHost.exe"),
            LocalPort: 8080,
            RemotePort: null,
            LocalAddresses: "*",
            RemoteAddresses: "10.20.0.0/16,LocalSubnet");
        var current = ToState(desired) with
        {
            Name = desired.Name.ToLowerInvariant(),
            ProgramPath = Path.Combine(directory, ".", "D2RHOST.EXE"),
            LocalPorts = " 8080 ",
            RemotePorts = "Any",
            LocalAddresses = "Any",
            RemoteAddresses = " localsubnet, 10.20.0.0/16, LOCALSUBNET "
        };

        Assert.True(HostFirewallRules.Matches(current, desired));
        Assert.False(HostFirewallRules.Matches(current with { Allow = false }, desired));
    }

    private static HostConfig CreateConfig(string mode)
    {
        return new HostConfig
        {
            Mode = mode,
            NodeId = mode == HostConfig.WorkerMode ? "server-b" : "server-a",
            MasterUrl = mode == HostConfig.WorkerMode ? "ws://192.0.2.10:8080/node" : null,
            HttpPort = 8080,
            WindowsFirewall = new WindowsFirewallConfig
            {
                Manage = true,
                TrustedNetworks = ["LocalSubnet", "10.20.0.0/16"],
                ReconcileSeconds = 30,
                WasExplicitlyConfigured = true,
                OwnerId = TestOwnerId
            }
        };
    }

    private static HostFirewallManager CreateManager(
        HostConfig config,
        IHostFirewallBackend backend,
        IReadOnlyList<string>? localAddresses = null,
        TimeProvider? timeProvider = null)
    {
        return new HostFirewallManager(
            config,
            backend,
            new FakeHostNetworkAddressProvider(localAddresses ?? TestLocalAddresses),
            NullLogger<HostFirewallManager>.Instance,
            timeProvider);
    }

    private static HostFirewallRuleSpec[] BuildDesiredForCurrentProcess(HostConfig config)
    {
        var processPath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Current process path is unavailable in the test host.");
        return HostFirewallRules.BuildDesired(config, processPath, TestLocalAddresses);
    }

    private static HostFirewallRuleState ToState(HostFirewallRuleSpec rule)
    {
        return new HostFirewallRuleState(
            Name: rule.Name,
            DisplayName: rule.DisplayName,
            Description: rule.Description,
            Group: rule.Group,
            Direction: rule.Direction,
            ProgramPath: rule.ProgramPath,
            Protocol: HostFirewallRules.TcpProtocol,
            LocalPorts: rule.LocalPort?.ToString() ?? "*",
            RemotePorts: rule.RemotePort?.ToString() ?? "*",
            LocalAddresses: rule.LocalAddresses,
            RemoteAddresses: rule.RemoteAddresses,
            Profiles: HostFirewallRules.AllProfiles,
            Enabled: true,
            Allow: true,
            EdgeTraversal: false);
    }

    private static void AssertBackendWasNotCalled(FakeHostFirewallBackend backend)
    {
        Assert.Equal(0, backend.PolicyCalls);
        Assert.Equal(0, backend.ListCalls);
        Assert.Empty(backend.UpsertCalls);
        Assert.Empty(backend.RemoveCalls);
    }

    private sealed class FakeHostFirewallBackend(bool isSupported) : IHostFirewallBackend
    {
        public bool IsSupported { get; } = isSupported;

        public List<HostFirewallRuleState> Rules { get; } = [];

        public List<HostFirewallRuleSpec> UpsertCalls { get; } = [];

        public List<string> RemoveCalls { get; } = [];

        public Queue<HostFirewallPolicyState> PolicyStates { get; } = [];

        public HostFirewallPolicyState PolicyState { get; set; } = EffectivePrivatePolicy;

        public int PolicyCalls { get; private set; }

        public int ListCalls { get; private set; }

        public bool PersistUpserts { get; set; } = true;

        public Exception? UpsertException { get; set; }

        public HostFirewallPolicyState GetPolicyState()
        {
            PolicyCalls++;
            return PolicyStates.Count > 0 ? PolicyStates.Dequeue() : PolicyState;
        }

        public IReadOnlyList<HostFirewallRuleState> ListRules()
        {
            ListCalls++;
            return Rules.ToArray();
        }

        public void Upsert(HostFirewallRuleSpec rule)
        {
            UpsertCalls.Add(rule);
            if (UpsertException is not null)
            {
                throw UpsertException;
            }

            if (!PersistUpserts)
            {
                return;
            }

            var index = Rules.FindIndex(candidate =>
                string.Equals(candidate.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
            var state = ToState(rule);
            if (index >= 0)
            {
                Rules[index] = state;
            }
            else
            {
                Rules.Add(state);
            }
        }

        public void Remove(string name)
        {
            RemoveCalls.Add(name);
            Rules.RemoveAll(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FakeHostNetworkAddressProvider(IReadOnlyList<string> addresses)
        : IHostNetworkAddressProvider
    {
        public IReadOnlyList<string> GetLocalAddresses() => addresses;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
