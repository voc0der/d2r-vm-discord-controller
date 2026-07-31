using AgentCommon;
using D2RHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace D2RAgent.Tests;

public sealed class VmPowerLifecycleCoordinatorTests
{
    [Fact]
    public async Task RunningVmsAreJournaledStoppedAndRestoredWhileOffVmsAreUntouched()
    {
        using var fixture = new LifecycleFixture(
            ("running", "vm-running", "Running"),
            ("off", "vm-off", "Off"));

        var preparation = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.True(preparation.Ok, preparation.Message);
        Assert.Equal(["vm-running"], preparation.StoppedVmNames);
        Assert.Equal(["vm-running"], fixture.HyperV.StopCalls);
        Assert.Empty(fixture.HyperV.StartCalls);
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-running"));
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-off"));
        Assert.Equal(["vm-running"], fixture.Database.GetPendingVmResume(fixture.Config.NodeId));

        var restore = await fixture.Coordinator.RestorePendingAsync();

        Assert.True(restore.Complete);
        Assert.Equal(["vm-running"], restore.RestoredVmNames);
        Assert.Empty(restore.AlreadyRunningVmNames);
        Assert.Empty(restore.PendingVmNames);
        Assert.Equal(["vm-running"], fixture.HyperV.StartCalls);
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-running"));
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-off"));
    }

    [Fact]
    public async Task TransitionalStateAbortsBeforeAnyVmIsStoppedOrJournaled()
    {
        using var fixture = new LifecycleFixture(
            ("running", "vm-running", "Running"),
            ("transitioning", "vm-transitioning", "Starting"));

        var preparation = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.False(preparation.Ok);
        Assert.Contains("state is Starting", preparation.Message, StringComparison.Ordinal);
        Assert.Empty(preparation.StoppedVmNames);
        Assert.Empty(fixture.HyperV.StopCalls);
        Assert.Empty(fixture.HyperV.StartCalls);
        Assert.Empty(fixture.Database.GetPendingVmResume(fixture.Config.NodeId));
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-running"));
    }

    [Fact]
    public async Task PartialStopFailureRollsBackStoppedVmsAndClearsTheJournal()
    {
        using var fixture = new LifecycleFixture(
            ("a", "vm-a", "Running"),
            ("b", "vm-b", "Running"));
        fixture.HyperV.StopFailures.Add("vm-b");

        var preparation = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.False(preparation.Ok);
        Assert.Equal(["vm-a"], preparation.StoppedVmNames);
        Assert.Equal(["vm-a", "vm-b"], fixture.HyperV.StopCalls);
        Assert.Equal(["vm-a"], fixture.HyperV.StartCalls);
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-a"));
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-b"));
        Assert.Empty(fixture.Database.GetPendingVmResume(fixture.Config.NodeId));
        Assert.Contains("already stopped were restored", preparation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingRestoreSurvivesAcrossAppDbAndCoordinatorInstances()
    {
        using var fixture = new LifecycleFixture(("persisted", "vm-persisted", "Running"));

        var preparation = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.True(preparation.Ok, preparation.Message);
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-persisted"));
        Assert.Equal(["vm-persisted"], fixture.Database.GetPendingVmResume(fixture.Config.NodeId));

        var reopenedDatabase = new AppDb(fixture.Config);
        var restartedCoordinator = CreateCoordinator(fixture.Config, reopenedDatabase, fixture.HyperV);

        Assert.Equal(["vm-persisted"], restartedCoordinator.GetPendingVmNames());

        var restore = await restartedCoordinator.RestorePendingAsync();

        Assert.True(restore.Complete);
        Assert.Equal(["vm-persisted"], restore.RestoredVmNames);
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-persisted"));
        Assert.Empty(reopenedDatabase.GetPendingVmResume(fixture.Config.NodeId));
        Assert.Empty(fixture.Database.GetPendingVmResume(fixture.Config.NodeId));
    }

    [Fact]
    public async Task AlreadyRunningPendingVmIsClearedWithoutIssuingAnotherStart()
    {
        using var fixture = new LifecycleFixture(("running", "vm-running", "Running"));
        fixture.Database.ReplacePendingVmResume(fixture.Config.NodeId, ["vm-running"]);

        var restore = await fixture.Coordinator.RestorePendingAsync();

        Assert.True(restore.Complete);
        Assert.Empty(restore.RestoredVmNames);
        Assert.Equal(["vm-running"], restore.AlreadyRunningVmNames);
        Assert.Empty(restore.PendingVmNames);
        Assert.Empty(fixture.HyperV.StartCalls);
        Assert.Empty(fixture.Database.GetPendingVmResume(fixture.Config.NodeId));
        Assert.Equal("Running", fixture.HyperV.StateOf("vm-running"));
    }

    [Fact]
    public async Task SecondPreparationCannotConsumeTheJournalOfAnArmedHostTransition()
    {
        using var fixture = new LifecycleFixture(("running", "vm-running", "Running"));

        var first = await fixture.Coordinator.PrepareForHostPowerActionAsync();
        var second = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.True(first.Ok, first.Message);
        Assert.False(second.Ok);
        Assert.Contains("already prepared", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-running"));
        Assert.Equal(["vm-running"], fixture.Database.GetPendingVmResume(fixture.Config.NodeId));
        Assert.Equal(["vm-running"], fixture.HyperV.StopCalls);
        Assert.Empty(fixture.HyperV.StartCalls);

        var restore = await fixture.Coordinator.RestorePendingAsync();
        var next = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.True(restore.Complete);
        Assert.True(next.Ok, next.Message);
        Assert.Equal("Off", fixture.HyperV.StateOf("vm-running"));
        Assert.Equal(2, fixture.HyperV.StopCalls.Count);
    }

    [Fact]
    public async Task EmptyVmSetStillArmsUntilThePowerTransitionCompletes()
    {
        using var fixture = new LifecycleFixture();

        var first = await fixture.Coordinator.PrepareForHostPowerActionAsync();
        var overlapping = await fixture.Coordinator.PrepareForHostPowerActionAsync();
        var completion = await fixture.Coordinator.RestorePendingAsync();
        var next = await fixture.Coordinator.PrepareForHostPowerActionAsync();

        Assert.True(first.Ok, first.Message);
        Assert.False(overlapping.Ok);
        Assert.True(completion.Complete);
        Assert.True(next.Ok, next.Message);
    }

    private static VmPowerLifecycleCoordinator CreateCoordinator(
        HostConfig config,
        AppDb database,
        ILocalVmPowerOperations hyperV)
    {
        return new VmPowerLifecycleCoordinator(
            config,
            database,
            hyperV,
            NullLogger<VmPowerLifecycleCoordinator>.Instance);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-vm-power-lifecycle-tests-" + Guid.NewGuid().ToString("N"));

        public LifecycleFixture(params (string AccountKey, string VmName, string State)[] vms)
        {
            Directory.CreateDirectory(_directory);
            Config = new HostConfig
            {
                Mode = HostConfig.MasterMode,
                NodeId = "server-a",
                DisableDiscord = true,
                DatabasePath = Path.Combine(_directory, "d2r-host.sqlite"),
                Accounts = vms.ToDictionary(
                    vm => vm.AccountKey,
                    vm => new AccountConfig
                    {
                        AgentId = "agent-" + vm.AccountKey,
                        NodeId = "server-a",
                        VmName = vm.VmName
                    },
                    StringComparer.OrdinalIgnoreCase)
            };
            HyperV = new FakeLocalVmPowerOperations(
                vms.Select(vm => (vm.VmName, vm.State)));
            Database = new AppDb(Config);
            Coordinator = CreateCoordinator(Config, Database, HyperV);
        }

        public HostConfig Config { get; }

        public AppDb Database { get; }

        public FakeLocalVmPowerOperations HyperV { get; }

        public VmPowerLifecycleCoordinator Coordinator { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeLocalVmPowerOperations : ILocalVmPowerOperations
    {
        private readonly Dictionary<string, string> _states;

        public FakeLocalVmPowerOperations(IEnumerable<(string VmName, string State)> states)
        {
            _states = states.ToDictionary(
                pair => pair.VmName,
                pair => pair.State,
                StringComparer.OrdinalIgnoreCase);
        }

        public List<string> StopCalls { get; } = [];

        public List<string> StartCalls { get; } = [];

        public HashSet<string> StopFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string StateOf(string vmName) => _states[vmName];

        public Task<VmPowerStateResult> GetPowerStateAsync(
            string vmName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _states.TryGetValue(vmName, out var state)
                    ? VmPowerStateResult.Success(state)
                    : VmPowerStateResult.Failure($"VM not found: {vmName}"));
        }

        public Task<CommandResult> StopAsync(
            string vmName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls.Add(vmName);
            if (StopFailures.Contains(vmName))
            {
                return Task.FromResult(CommandResult.Failure($"Stop failed for {vmName}"));
            }

            _states[vmName] = "Off";
            return Task.FromResult(CommandResult.Success($"Stopped {vmName}"));
        }

        public Task<CommandResult> StartAsync(
            string vmName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls.Add(vmName);
            _states[vmName] = "Running";
            return Task.FromResult(CommandResult.Success($"Started {vmName}"));
        }
    }
}
