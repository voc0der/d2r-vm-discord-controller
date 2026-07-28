using System.Text.Json;
using AgentCommon;
using D2RHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace D2RAgent.Tests;

// Satellite auto-update used to be purely local: each D2RHost offered self_update to an agent as
// it authenticated. That leaves worker-owned VM agents entirely dependent on their own worker,
// and a worker that could not complete its own update check disabled updates for every agent on
// that node - for the whole life of the process, reported only at Debug level.
public sealed class FleetAgentUpdateTests
{
    // A worker only ever updated when someone restarted it by hand, because the master's
    // authentication hook ignored anything that was not a VM agent and the worker's own command
    // surface had no self_update at all. A node stuck on an old build is exactly the one whose
    // local satellite auto-update has quietly stopped working, so this is the load-bearing half.
    // The authentication hook now offers to worker nodes too, but a node old enough to predate
    // this command still answers "unsupported" and needs one manual update to break the cycle.
    [Fact]
    public async Task WorkerAcceptsSelfUpdateFromTheMaster()
    {
        using var fixture = new WorkerFixture();

        var result = await fixture.Operations.HandleCommandAsync(
            new CommandRequest("cmd-1", "self_update", default), CancellationToken.None);

        Assert.DoesNotContain("Unsupported worker command", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerStillRejectsGenuinelyUnknownCommands()
    {
        using var fixture = new WorkerFixture();

        var result = await fixture.Operations.HandleCommandAsync(
            new CommandRequest("cmd-2", "not_a_real_command", default), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Unsupported worker command", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // An update is only announced when the satellite actually started one. A check that simply
    // reports "already current" must not post an update notification to Discord.
    [Fact]
    public void UpdateIsOnlyAnnouncedWhenTheSatelliteActuallyStartedOne()
    {
        var started = JsonSerializer.SerializeToElement(new
        {
            updateStarted = true,
            currentVersion = "0.2.207",
            latestVersion = "0.2.212",
            logPath = @"C:\Temp\update.log"
        });
        var alreadyCurrent = JsonSerializer.SerializeToElement(new
        {
            updateStarted = false,
            currentVersion = "0.2.212"
        });

        Assert.True(FleetAgentUpdater.TryReadSelfUpdateStarted(
            started, out var current, out var latest, out var logPath));
        Assert.Equal("0.2.207", current);
        Assert.Equal("0.2.212", latest);
        Assert.Equal(@"C:\Temp\update.log", logPath);

        Assert.False(FleetAgentUpdater.TryReadSelfUpdateStarted(alreadyCurrent, out _, out _, out _));
        Assert.False(FleetAgentUpdater.TryReadSelfUpdateStarted(null, out _, out _, out _));
    }

    [Fact]
    public void StartedUpdateTellsAnOldWorkerAboutTheOneTimeBootstrap()
    {
        var message = SatelliteUpdateNotifications.FormatStarted(
            "server-b",
            isNode: true,
            reportedVersion: "0.2.216",
            currentVersion: "0.2.216",
            latestVersion: "0.2.220",
            logPath: @"C:\Temp\update.log");

        Assert.Contains("update started", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still running v0.2.216", message);
        Assert.Contains("cannot stop the host process", message);
        Assert.Contains("/d2r system restart node:server-b", message);
    }

    [Theory]
    [InlineData(true, "0.2.220")]
    [InlineData(false, "0.2.216")]
    public void CurrentWorkersAndVmAgentsDoNotReceiveWorkerBootstrapInstructions(
        bool isNode,
        string reportedVersion)
    {
        var message = SatelliteUpdateNotifications.FormatStarted(
            "satellite",
            isNode,
            reportedVersion,
            currentVersion: reportedVersion,
            latestVersion: "0.2.221",
            logPath: null);

        Assert.DoesNotContain("cannot stop the host process", message);
    }

    // The sweep must not run at all when this host could not verify its own build, because it
    // would then be pushing satellites toward a release it never confirmed exists.
    [Fact]
    public async Task SweepDoesNotStartWhenThisHostCouldNotCheckItsOwnUpdate()
    {
        using var fixture = new WorkerFixture();
        var updater = new FleetAgentUpdater(
            fixture.Config,
            new AgentAutoUpdateState(Enabled: false, Reason: "Update check failed for tests."),
            fixture.Fleet,
            fixture.Registry,
            new DiscordNotificationQueue(),
            new SatelliteUpdateGate(),
            NullLogger<FleetAgentUpdater>.Instance);

        await updater.StartAsync(CancellationToken.None);
        await updater.StopAsync(CancellationToken.None);

        // No agents are connected in this fixture, so the sweep has nothing to offer either way -
        // what matters is that StartAsync returned without launching the loop.
        Assert.Equal(0, await updater.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SweepOffersNothingWhenNoAgentIsConnected()
    {
        using var fixture = new WorkerFixture();
        var updater = new FleetAgentUpdater(
            fixture.Config,
            new AgentAutoUpdateState(Enabled: true, Reason: "ok"),
            fixture.Fleet,
            fixture.Registry,
            new DiscordNotificationQueue(),
            new SatelliteUpdateGate(),
            NullLogger<FleetAgentUpdater>.Instance);

        Assert.Equal(0, await updater.SweepAsync(CancellationToken.None));
    }

    private sealed class WorkerFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "d2r-fleet-update-tests-" + Guid.NewGuid().ToString("N"));

        public WorkerFixture()
        {
            Directory.CreateDirectory(_directory);
            Config = new HostConfig
            {
                Mode = HostConfig.WorkerMode,
                NodeId = "netrunner",
                DisableDiscord = true,
                MasterUrl = "ws://master:8080/node",
                MasterSharedSecret = "worker-secret-1234",
                DatabasePath = Path.Combine(_directory, "worker.sqlite")
            };
            Database = new AppDb(Config);
            Registry = new AgentRegistry(
                Config,
                new AgentAutoUpdateState(false, "Disabled for tests."),
                new DiscordNotificationQueue(),
                new SatelliteUpdateGate(),
                Database,
                NullLogger<AgentRegistry>.Instance);
            Fleet = new FleetRegistry(Config, Registry, NullLogger<FleetRegistry>.Instance);
            Operations = new WorkerNodeOperations(
                Config,
                Registry,
                new HyperVOperations(Config),
                new HostSystemOperations(NullLogger<HostSystemOperations>.Instance),
                new HostRuntimeOptions(Path.Combine(_directory, "worker.config.json"), []));
        }

        public HostConfig Config { get; }
        public AppDb Database { get; }
        public AgentRegistry Registry { get; }
        public FleetRegistry Fleet { get; }
        public WorkerNodeOperations Operations { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
