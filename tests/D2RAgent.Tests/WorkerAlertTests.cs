using System.Text.Json;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// A worker has no Discord of its own. Anything that fails after a command has already answered -
// a sleep that never suspends being the motivating case - had no way to reach an operator at all,
// because there is no command result left to fail and the worker's log sits on the machine nobody
// is looking at. Alerts ride the heartbeat the worker already sends.
public sealed class WorkerAlertTests
{
    [Fact]
    public async Task StatusCarriesQueuedAlertsToTheMaster()
    {
        using var fixture = new WorkerAlertFixture();
        fixture.System.ReportSleepFailure("Host did not suspend.");

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None);

        var alert = Assert.Single(status.Alerts!);
        Assert.Contains("Host did not suspend.", alert, StringComparison.Ordinal);
        Assert.Contains(fixture.NodeId, alert, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlertsAreClearedOnceSentSoTheyArriveExactlyOnce()
    {
        using var fixture = new WorkerAlertFixture();
        fixture.System.ReportSleepFailure("Host did not suspend.");

        var first = await fixture.Operations.GetStatusAsync(CancellationToken.None);
        var second = await fixture.Operations.GetStatusAsync(CancellationToken.None);

        Assert.Single(first.Alerts!);
        Assert.Empty(second.Alerts!);
    }

    [Fact]
    public async Task AQuietWorkerSendsNoAlerts()
    {
        using var fixture = new WorkerAlertFixture();

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None);

        Assert.Empty(status.Alerts!);
    }

    [Fact]
    public async Task TheAlertMailboxIsBoundedSoAnAbsentMasterCannotGrowItForever()
    {
        using var fixture = new WorkerAlertFixture();
        for (var i = 0; i < 50; i++)
        {
            fixture.System.ReportSleepFailure($"failure {i}");
        }

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Alerts!.Count <= 10, $"Expected a bounded mailbox, got {status.Alerts!.Count}.");
        // The newest failures are the ones worth keeping.
        Assert.Contains("failure 49", status.Alerts![^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusSerializesAlertsUnderTheNameTheMasterReads()
    {
        using var fixture = new WorkerAlertFixture();
        fixture.System.ReportSleepFailure("Host did not suspend.");
        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None);

        // The master parses this by name off the raw heartbeat JSON, so the casing matters.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(status));
        var found = document.RootElement.TryGetProperty("alerts", out var alerts)
            || document.RootElement.TryGetProperty("Alerts", out alerts);

        Assert.True(found, "Worker status must expose an alerts array.");
        Assert.Equal(JsonValueKind.Array, alerts.ValueKind);
        Assert.Single(alerts.EnumerateArray());
    }

    private sealed class WorkerAlertFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "d2r-worker-alert-tests-" + Guid.NewGuid().ToString("N"));

        public WorkerAlertFixture()
        {
            Directory.CreateDirectory(_directory);
            var config = new HostConfig
            {
                Mode = HostConfig.WorkerMode,
                NodeId = NodeId,
                DisableDiscord = true,
                MasterUrl = "ws://master:8080/node",
                MasterSharedSecret = "worker-secret-1234",
                DatabasePath = Path.Combine(_directory, "worker.sqlite")
            };
            Database = new AppDb(config);
            var registry = new AgentRegistry(
                config,
                new AgentAutoUpdateState(false, "Disabled for tests."),
                new DiscordNotificationQueue(),
                new SatelliteUpdateGate(),
                Database,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRegistry>.Instance);
            System = new HostSystemOperations(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HostSystemOperations>.Instance);
            Operations = new WorkerNodeOperations(
                config,
                registry,
                new HyperVOperations(config),
                System,
                new HostRuntimeOptions(Path.Combine(_directory, "worker.config.json"), []));
        }

        public string NodeId => "netrunner";
        public AppDb Database { get; }
        public HostSystemOperations System { get; }
        public WorkerNodeOperations Operations { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
