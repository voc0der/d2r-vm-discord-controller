using System.Text.Json;
using AgentCommon;
using D2RHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace D2RAgent.Tests;

public sealed class HostTopologyTests
{
    private const string ValidSecret = "test-secret-1234";

    [Fact]
    public void ProgrammaticFirewallConfigUsesSecureRuntimeMetadataDefaults()
    {
        var firewall = new WindowsFirewallConfig();

        Assert.True(firewall.WasExplicitlyConfigured);
        Assert.Equal("default", firewall.OwnerId);
        Assert.Equal(["LocalSubnet"], firewall.TrustedNetworks);
    }

    [Fact]
    public void LegacyConfigDefaultsToLocalMaster()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            agents = new { },
            accounts = new { }
        });

        Assert.Equal(HostConfig.MasterMode, config.Mode);
        Assert.Equal("local", config.NodeId);
        Assert.True(config.IsMaster);
        Assert.False(config.IsWorker);
        Assert.True(config.WindowsFirewall.Manage);
        Assert.Equal(["LocalSubnet"], config.WindowsFirewall.TrustedNetworks);
        Assert.Equal(30, config.WindowsFirewall.ReconcileSeconds);
        Assert.False(config.WindowsFirewall.WasExplicitlyConfigured);
        Assert.Matches("^[0-9a-f]{12}$", config.WindowsFirewall.OwnerId);
    }

    [Fact]
    public void FirewallTrustedNetworksAreNormalized()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            windowsFirewall = new
            {
                trustedNetworks = new[] { " 10.0.0.0/8 ", "localsubnet", "LocalSubnet" },
                reconcileSeconds = 60
            }
        });

        Assert.Equal(["10.0.0.0/8", "LocalSubnet"], config.WindowsFirewall.TrustedNetworks);
        Assert.Equal(60, config.WindowsFirewall.ReconcileSeconds);
        Assert.True(config.WindowsFirewall.WasExplicitlyConfigured);
    }

    [Fact]
    public void FirewallTrustedNetworksCanonicalizeSortAndDeduplicate()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            windowsFirewall = new
            {
                trustedNetworks = new[]
                {
                    "localsubnet",
                    "2001:0DB8:0000:0000:0000:0000:0000:0001/064",
                    "10.2.39.65",
                    "2001:db8::1/64",
                    "2001:0DB8:0000:0000:0000:0000:0000:0001",
                    "2001:db8::1",
                    "10.2.39.65/032"
                }
            }
        });

        Assert.Equal(
            ["10.2.39.65", "10.2.39.65/32", "2001:db8::/64", "2001:db8::1", "LocalSubnet"],
            config.WindowsFirewall.TrustedNetworks);
    }

    [Fact]
    public void FirewallTrustedNetworkCidrsAreCanonicalizedToNetworkPrefixes()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            windowsFirewall = new
            {
                trustedNetworks = new[] { "10.2.39.65/24", "2001:db8:42:7::1234/64" }
            }
        });

        Assert.Equal(
            ["10.2.39.0/24", "2001:db8:42:7::/64"],
            config.WindowsFirewall.TrustedNetworks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Any")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.2.39.65/0")]
    [InlineData("10.2.39.65/00")]
    [InlineData("0000:0000:0000:0000:0000:0000:0000:0001/0")]
    [InlineData("2001:db8::1/00")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/8/9")]
    [InlineData("not-a-network")]
    public void ManagedFirewallRejectsUnsafeOrInvalidTrustedNetworks(string trustedNetwork)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            disableDiscord = true,
            windowsFirewall = new
            {
                trustedNetworks = new[] { trustedNetwork }
            }
        }));

        Assert.Contains("windowsFirewall.trustedNetworks", exception.Message);
    }

    [Fact]
    public void ExternallyManagedFirewallDoesNotRequireTrustedNetworks()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            windowsFirewall = new
            {
                manage = false,
                trustedNetworks = Array.Empty<string>()
            }
        });

        Assert.False(config.WindowsFirewall.Manage);
        Assert.Empty(config.WindowsFirewall.TrustedNetworks);
        Assert.True(config.WindowsFirewall.WasExplicitlyConfigured);
    }

    [Fact]
    public void FirewallOwnerIdIsStableForCanonicalPathAndDifferentAcrossPaths()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-host-firewall-owner-tests-" + Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(directory, "first", "d2r-host.config.json");
        var equivalentFirstPath = Path.Combine(directory, "first", ".", "d2r-host.config.json");
        var secondPath = Path.Combine(directory, "second", "d2r-host.config.json");
        var json = JsonSerializer.Serialize(new
        {
            disableDiscord = true,
            windowsFirewall = new { }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        File.WriteAllText(firstPath, json);
        File.WriteAllText(secondPath, json);

        try
        {
            var first = HostConfigLoader.Load(firstPath);
            var equivalentFirst = HostConfigLoader.Load(equivalentFirstPath);
            var second = HostConfigLoader.Load(secondPath);

            Assert.Matches("^[0-9a-f]{12}$", first.WindowsFirewall.OwnerId);
            Assert.Equal(first.WindowsFirewall.OwnerId, equivalentFirst.WindowsFirewall.OwnerId);
            Assert.NotEqual(first.WindowsFirewall.OwnerId, second.WindowsFirewall.OwnerId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavingLegacyConfigDoesNotSilentlyOptIntoScopedFirewallRules()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-host-firewall-save-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "d2r-host.config.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            disableDiscord = true,
            agents = new { },
            accounts = new { }
        }));

        try
        {
            var config = HostConfigLoader.Load(path);
            HostConfigLoader.Save(path, config);

            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(saved.RootElement.TryGetProperty("windowsFirewall", out _));
            Assert.False(HostConfigLoader.Load(path).WindowsFirewall.WasExplicitlyConfigured);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void HostRejectsInvalidHttpPort(int httpPort)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            disableDiscord = true,
            httpPort
        }));

        Assert.Contains("httpPort must be between 1 and 65535", exception.Message);
    }

    [Fact]
    public void LegacyConfigMayRetainVmAgentNamedLocal()
    {
        var config = LoadConfig(new
        {
            disableDiscord = true,
            agents = new Dictionary<string, object>
            {
                ["local"] = new { kind = "vm", sharedSecret = ValidSecret }
            },
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "local" }
            }
        });

        Assert.Equal("local", config.NodeId);
        Assert.Equal("local", config.Accounts["hc1"].AgentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkerRequiresExplicitNonEmptyNodeId(string? nodeId)
    {
        var document = new Dictionary<string, object?>
        {
            ["mode"] = HostConfig.WorkerMode,
            ["masterUrl"] = "ws://master.example:8080",
            ["masterSharedSecret"] = ValidSecret
        };

        if (nodeId is not null)
        {
            document["nodeId"] = nodeId;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(document));

        Assert.Contains("nodeId is required", exception.Message);
    }

    [Theory]
    [InlineData("ws://master.example:8080")]
    [InlineData("wss://master.example/control")]
    [InlineData("  ws://master.example:8080  ")]
    public void WorkerAcceptsAbsoluteWebSocketMasterUrlsAndDisablesDiscord(string masterUrl)
    {
        var config = LoadConfig(new
        {
            mode = HostConfig.WorkerMode,
            nodeId = "server-b",
            masterUrl,
            masterSharedSecret = ValidSecret,
            disableDiscord = false,
            discordToken = "must-not-be-used-by-a-worker"
        });

        Assert.True(config.IsWorker);
        Assert.True(config.DisableDiscord);
        Assert.Equal(masterUrl.Trim(), config.MasterUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("master.example:8080")]
    [InlineData("/node")]
    [InlineData("ws:///node")]
    [InlineData("ws://master.example:0/node")]
    [InlineData("http://master.example:8080")]
    [InlineData("https://master.example")]
    public void WorkerRejectsMissingOrNonWebSocketMasterUrl(string? masterUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.WorkerMode,
            nodeId = "server-b",
            masterUrl,
            masterSharedSecret = ValidSecret
        }));

        Assert.Contains("masterUrl", exception.Message);
        Assert.Contains("ws:// or wss://", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("12345678901")]
    public void WorkerRequiresMasterSecretOfAtLeastTwelveCharacters(string? masterSharedSecret)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.WorkerMode,
            nodeId = "server-b",
            masterUrl = "wss://master.example",
            masterSharedSecret
        }));

        Assert.Contains("masterSharedSecret must be at least 12 characters", exception.Message);
    }

    [Fact]
    public void MasterAcceptsHostAgent()
    {
        var config = LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = new Dictionary<string, object>
            {
                ["server-b"] = new
                {
                    kind = "host",
                    sharedSecret = ValidSecret
                }
            }
        });

        Assert.Equal("host", config.Agents["server-b"].Kind);
    }

    [Theory]
    [InlineData(HostConfig.MasterMode, "host")]
    [InlineData(HostConfig.WorkerMode, "vm")]
    public void NodeIdCannotCollideWithAnyLocalAgentId(string mode, string agentKind)
    {
        var document = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["nodeId"] = "server-a",
            ["disableDiscord"] = true,
            ["agents"] = new Dictionary<string, object>
            {
                ["SERVER-A"] = new { kind = agentKind, sharedSecret = ValidSecret }
            },
            ["accounts"] = new { }
        };
        if (mode == HostConfig.WorkerMode)
        {
            document["masterUrl"] = "ws://master.example:8080/node";
            document["masterSharedSecret"] = ValidSecret;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(document));

        Assert.Contains("conflicts with this host's nodeId", exception.Message);
    }

    [Fact]
    public void AgentIdsAreCaseInsensitiveForAccountResolution()
    {
        var config = LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = new Dictionary<string, object>
            {
                ["VM-HC1"] = new { kind = "vm", sharedSecret = ValidSecret }
            },
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "vm-hc1" }
            }
        });

        Assert.Same(config.Agents["VM-HC1"], config.Agents["vm-hc1"]);
        Assert.Equal("vm-hc1", config.Accounts["hc1"].AgentId);
    }

    [Fact]
    public void CaseOnlyDuplicateAgentIdsAreRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = new Dictionary<string, object>
            {
                ["vm-hc1"] = new { kind = "vm", sharedSecret = ValidSecret },
                ["VM-HC1"] = new { kind = "vm", sharedSecret = ValidSecret }
            },
            accounts = new { }
        }));

        Assert.Contains("Duplicate agent ID", exception.Message);
        Assert.Contains("case-insensitive", exception.Message);
    }

    [Fact]
    public void ExactDuplicateAgentJsonPropertiesAreRejectedBeforeDeserialization()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfigJson($$"""
            {
              "mode": "master",
              "nodeId": "server-a",
              "disableDiscord": true,
              "agents": {
                "vm-hc1": { "kind": "vm", "sharedSecret": "{{ValidSecret}}" },
                "vm-hc1": { "kind": "vm", "sharedSecret": "{{ValidSecret}}" }
              },
              "accounts": {}
            }
            """));

        Assert.Contains("Duplicate agent ID", exception.Message);
    }

    [Fact]
    public void WorkerRejectsHostAgent()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.WorkerMode,
            nodeId = "server-b",
            masterUrl = "ws://master.example:8080",
            masterSharedSecret = ValidSecret,
            agents = new Dictionary<string, object>
            {
                ["another-host"] = new
                {
                    kind = "host",
                    sharedSecret = ValidSecret
                }
            }
        }));

        Assert.Contains("must have kind \"vm\"", exception.Message);
    }

    [Fact]
    public void AccountWithoutNodeIdIsAssignedToItsLocalVmAgentNode()
    {
        var config = LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = VmAgents(),
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "vm-hc1" }
            }
        });

        Assert.Equal("server-a", config.Accounts["hc1"].NodeId);
    }

    [Fact]
    public void AccountCannotClaimAnotherNode()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = VmAgents(),
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "vm-hc1", nodeId = "server-b" }
            }
        }));

        Assert.Contains("belongs to node \"server-b\"", exception.Message);
        Assert.Contains("this host is node \"server-a\"", exception.Message);
    }

    [Fact]
    public void AccountCannotReferenceHostAgent()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = new Dictionary<string, object>
            {
                ["server-b"] = new
                {
                    kind = "host",
                    sharedSecret = ValidSecret
                }
            },
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "server-b" }
            }
        }));

        Assert.Contains("must reference a VM agent", exception.Message);
    }

    [Fact]
    public void AccountCannotReferenceMissingAgent()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LoadConfig(new
        {
            mode = HostConfig.MasterMode,
            nodeId = "server-a",
            disableDiscord = true,
            agents = new { },
            accounts = new Dictionary<string, object>
            {
                ["hc1"] = new { agentId = "missing-vm" }
            }
        }));

        Assert.Contains("references missing VM agent", exception.Message);
    }

    [Fact]
    public void ConfigWarningsNameUnmappedVmAgents()
    {
        var config = new HostConfig
        {
            Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["D2R_1"] = new() { Kind = "vm" },
                ["D2R_2"] = new() { Kind = "vm" }
            },
            Accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["hc1"] = new() { AgentId = "d2r_1" }
            }
        };

        var warnings = HostConfigLoader.GetWarnings(config);

        Assert.Contains(
            warnings,
            warning => warning.Contains("D2R_2", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warnings,
            warning => warning.Contains("D2R_1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigWarningsNameEveryAccountSharingOneVmAgent()
    {
        var config = new HostConfig
        {
            Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["D2R_1"] = new() { Kind = "vm" }
            },
            Accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["hc1"] = new() { AgentId = "D2R_1" },
                ["mule"] = new() { AgentId = "d2r_1" }
            }
        };

        var warning = Assert.Single(HostConfigLoader.GetWarnings(config));

        Assert.Contains("D2R_1", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hc1", warning, StringComparison.Ordinal);
        Assert.Contains("mule", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigWarningsDoNotRequireAccountsForHostAgents()
    {
        var config = new HostConfig
        {
            Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["server-b"] = new() { Kind = "host" }
            }
        };

        Assert.Empty(HostConfigLoader.GetWarnings(config));
    }

    [Fact]
    public async Task WorkerStatusProjectionOmitsMasterAndAgentSecrets()
    {
        using var fixture = new WorkerOperationsFixture();

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.False(json.Contains(fixture.MasterSecret, StringComparison.Ordinal));
        Assert.False(json.Contains(fixture.AgentSecret, StringComparison.Ordinal));
        Assert.False(json.Contains("sharedSecret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("server-b", status.NodeId);
        Assert.Single(status.Agents);
        Assert.Single(status.Accounts);
        Assert.Equal(90, status.VmCommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("", "Worker command is required")]
    [InlineData("not_a_worker_command", "Unsupported worker command")]
    public async Task WorkerOperationsRejectInvalidCommandsWithoutRunningSystemActions(
        string command,
        string expectedMessage)
    {
        using var fixture = new WorkerOperationsFixture();
        var request = new CommandRequest(
            "test-command",
            command,
            JsonSerializer.SerializeToElement(new { }));

        var result = await fixture.Operations.HandleCommandAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(expectedMessage, result.Message);
    }

    [Fact]
    public async Task WorkerOperationsRejectMalformedAgentCommandArguments()
    {
        using var fixture = new WorkerOperationsFixture();
        var request = new CommandRequest(
            "test-command",
            "agent_command",
            JsonSerializer.SerializeToElement("not-an-object"));

        var result = await fixture.Operations.HandleCommandAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("agent_command args must be a JSON object.", result.Message);
    }

    [Fact]
    public void AccountConnectivityDistinguishesOfflineMappingsFromConnectedUnmappedAgents()
    {
        var accounts = Enumerable.Range(1, 4).ToDictionary(
            index => $"hc{index}",
            index => new AccountConfig { AgentId = $"D2R_{index}" },
            StringComparer.OrdinalIgnoreCase);
        var agents = Enumerable.Range(1, 8)
            .Select(index => VmAgentSnapshot($"D2R_{index}", connected: index >= 5))
            .ToArray();

        var connectivity = FleetRegistry.ClassifyAccountConnectivity(accounts, agents);

        Assert.Empty(connectivity.Online);
        Assert.Equal(
            ["hc1", "hc2", "hc3", "hc4"],
            connectivity.Offline.Select(entry => entry.Key));
        Assert.Equal(
            ["D2R_1", "D2R_2", "D2R_3", "D2R_4"],
            connectivity.Offline.Select(entry => entry.Value.AgentId));
        Assert.Equal(
            ["D2R_5", "D2R_6", "D2R_7", "D2R_8"],
            connectivity.ConnectedUnaddressableAgents.Select(agent => agent.Id));
    }

    [Fact]
    public void AccountConnectivityMatchesMappedAgentIdsCaseInsensitively()
    {
        var account = new AccountConfig { AgentId = "d2r_1" };
        var accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["hc1"] = account
        };

        var connectivity = FleetRegistry.ClassifyAccountConnectivity(
            accounts,
            [VmAgentSnapshot("D2R_1", connected: true)]);

        var online = Assert.Single(connectivity.Online);
        Assert.Equal("hc1", online.Key);
        Assert.Same(account, online.Value);
        Assert.Empty(connectivity.Offline);
        Assert.Empty(connectivity.ConnectedUnaddressableAgents);
    }

    [Fact]
    public void AccountConnectivityDiagnosticsNameNodeAgentAndOfflineMapping()
    {
        var accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["hc1"] = new() { AgentId = "D2R_1" }
        };
        var agentNodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["d2r_5"] = "server-b"
        };
        var connectivity = FleetRegistry.ClassifyAccountConnectivity(
            accounts,
            [
                VmAgentSnapshot("D2R_1", connected: false),
                VmAgentSnapshot("D2R_5", connected: true)
            ],
            agentNodes);

        var unaddressable = Assert.Single(connectivity.ConnectedUnaddressableAgents);
        Assert.Equal("server-b", unaddressable.NodeId);
        Assert.Equal(
            [
                "Accounts: 0/1 available",
                "Connected VM agents not addressable by a fleet account: server-b/D2R_5"
            ],
            DiscordBot.FormatAccountConnectivityHealthLines(connectivity));

        var suffix = DiscordBot.FormatOfflineSkipSuffix(
            connectivity.Offline,
            connectivity.ConnectedUnaddressableAgents);
        Assert.Contains("server-b/D2R_5", suffix, StringComparison.Ordinal);
        Assert.Contains("hc1 -> D2R_1", suffix, StringComparison.Ordinal);
        Assert.True(
            suffix.IndexOf("server-b/D2R_5", StringComparison.Ordinal)
                < suffix.IndexOf("hc1 -> D2R_1", StringComparison.Ordinal));
    }

    [Fact]
    public void PersistedWorkerInventoryRemainsKnownAndOfflineAfterMasterRestart()
    {
        using var databaseFixture = new TemporaryDatabaseFixture();
        var config = new HostConfig
        {
            Mode = HostConfig.MasterMode,
            NodeId = "server-a",
            DisableDiscord = true,
            DatabasePath = databaseFixture.DatabasePath,
            Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["server-b"] = new()
                {
                    Kind = "host",
                    DisplayName = "Server B",
                    SharedSecret = ValidSecret
                }
            }
        };
        var database = new AppDb(config);
        var capturedAt = DateTimeOffset.UtcNow;
        var workerStatus = new WorkerNodeStatus(
            "server-b",
            "server-b-host",
            capturedAt,
            new MachineTelemetrySnapshot(null, null, null, null),
            [
                new WorkerNodeAgent(
                    "vm-hc2",
                    "vm",
                    "HC2",
                    null,
                    new AgentSnapshot(
                        "vm-hc2",
                        "vm",
                        "HC2",
                        "server-b-vm",
                        "1.0.0",
                        true,
                        capturedAt,
                        capturedAt,
                        "{}"))
            ],
            [new WorkerNodeAccount("hc2", "vm-hc2", "HC2", "d2r-hc-02", 2)],
            VmCommandTimeoutSeconds: 321);
        database.UpsertAgentStatus(
            "server-b",
            "host",
            connected: true,
            JsonSerializer.Serialize(workerStatus, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        // Constructing a fresh registry models a master restart: persisted status is
        // retained as inventory, while all socket-backed connectivity is reset.
        var restartedRegistry = new AgentRegistry(
            config,
            new AgentAutoUpdateState(false, "Disabled for tests."),
            new DiscordNotificationQueue(),
            database,
            NullLogger<AgentRegistry>.Instance);
        var fleet = new FleetRegistry(config, restartedRegistry, NullLogger<FleetRegistry>.Instance);

        var account = Assert.Single(fleet.Accounts);
        Assert.Equal("hc2", account.Key);
        Assert.Equal("vm-hc2", account.Value.AgentId);
        Assert.Equal("server-b", account.Value.NodeId);

        var connectivity = fleet.GetAccountConnectivity();
        Assert.Empty(connectivity.Online);
        Assert.Equal("hc2", Assert.Single(connectivity.Offline).Key);
        Assert.Empty(connectivity.ConnectedUnaddressableAgents);

        var remoteAgent = Assert.IsType<AgentSnapshot>(fleet.GetAgent("vm-hc2"));
        Assert.False(remoteAgent.Connected);
        var remoteNode = Assert.Single(fleet.NodeSnapshot(), node => node.Id == "server-b");
        Assert.False(remoteNode.Connected);
        Assert.Equal(0, remoteNode.AgentsConnected);
        Assert.Equal(1, remoteNode.AgentsConfigured);
        Assert.Equal(321, fleet.GetNodeVmCommandTimeoutSeconds("server-b"));
    }

    [Theory]
    [InlineData("server-a", "server-a")]
    [InlineData("vm-advertised", "vm-snapshot")]
    public void InvalidRemoteAgentIdentityIsExcludedFromFleet(
        string advertisedAgentId,
        string snapshotAgentId)
    {
        using var databaseFixture = new TemporaryDatabaseFixture();
        var config = new HostConfig
        {
            Mode = HostConfig.MasterMode,
            NodeId = "server-a",
            DisableDiscord = true,
            DatabasePath = databaseFixture.DatabasePath,
            Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["server-b"] = new() { Kind = "host", SharedSecret = ValidSecret }
            }
        };
        var database = new AppDb(config);
        var capturedAt = DateTimeOffset.UtcNow;
        var status = new WorkerNodeStatus(
            "server-b",
            "server-b-host",
            capturedAt,
            new MachineTelemetrySnapshot(null, null, null, null),
            [
                new WorkerNodeAgent(
                    advertisedAgentId,
                    "vm",
                    "Invalid VM",
                    null,
                    new AgentSnapshot(
                        snapshotAgentId,
                        "vm",
                        "Invalid VM",
                        null,
                        null,
                        true,
                        capturedAt,
                        capturedAt,
                        "{}"))
            ],
            [new WorkerNodeAccount("invalid", advertisedAgentId, "Invalid", "d2r-invalid", 1)]);
        database.UpsertAgentStatus(
            "server-b",
            "host",
            connected: false,
            JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var registry = new AgentRegistry(
            config,
            new AgentAutoUpdateState(false, "Disabled for tests."),
            new DiscordNotificationQueue(),
            database,
            NullLogger<AgentRegistry>.Instance);
        var fleet = new FleetRegistry(config, registry, NullLogger<FleetRegistry>.Instance);

        Assert.Empty(fleet.Accounts);
        Assert.Null(fleet.GetAgent(advertisedAgentId));
    }

    [Fact]
    public void InventoryAdvertisingAnotherNodeIdIsExcludedFromFleet()
    {
        using var fixture = new MasterFleetFixture();
        var capturedAt = DateTimeOffset.UtcNow;
        fixture.Database.UpsertAgentStatus(
            "server-b",
            "host",
            connected: false,
            JsonSerializer.Serialize(
                new WorkerNodeStatus(
                    "impostor-node",
                    "server-b-host",
                    capturedAt,
                    new MachineTelemetrySnapshot(null, null, null, null),
                    [
                        new WorkerNodeAgent(
                            "vm-hc2",
                            "vm",
                            "HC2",
                            null,
                            new AgentSnapshot(
                                "vm-hc2",
                                "vm",
                                "HC2",
                                null,
                                null,
                                true,
                                capturedAt,
                                capturedAt,
                                "{}"))
                    ],
                    [new WorkerNodeAccount("hc2", "vm-hc2", "HC2", "d2r-hc-02", 2)]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var fleet = fixture.CreateFleet();

        Assert.Empty(fleet.Accounts);
        Assert.Null(fleet.GetAgent("vm-hc2"));
    }

    [Fact]
    public async Task OfflineWorkerIsExcludedFromOnlineNodesAndRemoteVmCommandIsSkipped()
    {
        using var fixture = new MasterFleetFixture();
        var registry = fixture.CreateRegistry();
        var fleet = new FleetRegistry(
            fixture.Config,
            registry,
            NullLogger<FleetRegistry>.Instance);
        var operations = new FleetHostOperations(
            fixture.Config,
            registry,
            fleet,
            new HyperVOperations(fixture.Config),
            new HostSystemOperations(NullLogger<HostSystemOperations>.Instance));
        var account = new AccountConfig
        {
            NodeId = "server-b",
            AgentId = "vm-hc2",
            VmName = "d2r-hc-02"
        };

        var result = await operations.HandleCommandAsync(
            account,
            new CommandRequest(
                "test-command",
                "vm_start",
                JsonSerializer.SerializeToElement(new { vmName = "d2r-hc-02" })),
            CancellationToken.None);

        Assert.Equal(["server-a"], operations.NodeIds(onlineOnly: true));
        Assert.Equal(["server-a", "server-b"], operations.NodeIds(onlineOnly: false));
        Assert.False(result.Ok);
        Assert.Contains("worker \"server-b\" is offline", result.Message);
        Assert.Contains("skipped", result.Message);
    }

    private static Dictionary<string, object> VmAgents() => new()
    {
        ["vm-hc1"] = new
        {
            kind = "vm",
            sharedSecret = ValidSecret
        }
    };

    private static AgentSnapshot VmAgentSnapshot(string agentId, bool connected)
    {
        return new AgentSnapshot(
            agentId,
            "vm",
            agentId,
            HostName: null,
            Version: null,
            connected,
            ConnectedAt: null,
            LastSeenAt: null,
            LastStatusJson: null);
    }

    private static HostConfig LoadConfig(object document)
    {
        return LoadConfigJson(JsonSerializer.Serialize(document));
    }

    private static HostConfig LoadConfigJson(string json)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-host-topology-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "d2r-host.config.json");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(path, json);
            return HostConfigLoader.Load(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class WorkerOperationsFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-worker-operations-tests-" + Guid.NewGuid().ToString("N"));

        public WorkerOperationsFixture()
        {
            Directory.CreateDirectory(_directory);
            MasterSecret = "master-" + Guid.NewGuid().ToString("N");
            AgentSecret = "agent-" + Guid.NewGuid().ToString("N");

            var config = new HostConfig
            {
                Mode = HostConfig.WorkerMode,
                NodeId = "server-b",
                MasterUrl = "ws://master.example:8080",
                MasterSharedSecret = MasterSecret,
                DisableDiscord = true,
                DatabasePath = Path.Combine(_directory, "worker.sqlite"),
                Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["vm-hc1"] = new()
                    {
                        Kind = "vm",
                        DisplayName = "HC1",
                        SharedSecret = AgentSecret
                    }
                },
                Accounts = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hc1"] = new()
                    {
                        AgentId = "vm-hc1",
                        NodeId = "server-b",
                        DisplayName = "HC1",
                        VmName = "d2r-hc-01",
                        CharacterSlot = 1
                    }
                }
            };

            var database = new AppDb(config);
            var registry = new AgentRegistry(
                config,
                new AgentAutoUpdateState(false, "Disabled for tests."),
                new DiscordNotificationQueue(),
                database,
                NullLogger<AgentRegistry>.Instance);

            Operations = new WorkerNodeOperations(
                config,
                registry,
                new HyperVOperations(config),
                new HostSystemOperations(NullLogger<HostSystemOperations>.Instance));
        }

        public WorkerNodeOperations Operations { get; }
        public string MasterSecret { get; }
        public string AgentSecret { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TemporaryDatabaseFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-fleet-restart-tests-" + Guid.NewGuid().ToString("N"));

        public TemporaryDatabaseFixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "master.sqlite");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MasterFleetFixture : IDisposable
    {
        private readonly TemporaryDatabaseFixture _databaseFixture = new();

        public MasterFleetFixture()
        {
            Config = new HostConfig
            {
                Mode = HostConfig.MasterMode,
                NodeId = "server-a",
                DisableDiscord = true,
                DatabasePath = _databaseFixture.DatabasePath,
                Agents = new Dictionary<string, HostAgentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["server-b"] = new()
                    {
                        Kind = "host",
                        SharedSecret = ValidSecret
                    }
                }
            };
            Database = new AppDb(Config);
        }

        public HostConfig Config { get; }
        public AppDb Database { get; }

        public AgentRegistry CreateRegistry()
        {
            return new AgentRegistry(
                Config,
                new AgentAutoUpdateState(false, "Disabled for tests."),
                new DiscordNotificationQueue(),
                Database,
                NullLogger<AgentRegistry>.Instance);
        }

        public FleetRegistry CreateFleet()
        {
            return new FleetRegistry(
                Config,
                CreateRegistry(),
                NullLogger<FleetRegistry>.Instance);
        }

        public void Dispose()
        {
            _databaseFixture.Dispose();
        }
    }
}
