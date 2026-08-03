using AgentCommon;
using D2RAgent;
using System.Text.Json;
using Xunit;

namespace D2RAgent.Tests;

// These tests exercise the automation state that consumes the pixel detector. A pure image test
// cannot prove that a bounded wrapper preserves GammaCalibration, that a failed confirmation
// clears the first sighting, or that a confirmed repair remains actionable after D2R stops.
public sealed class GammaCalibrationWorkflowTests
{
    private static VmOperations NewVmOperations(bool repairEnabled = true)
    {
        return new VmOperations(new VmAgentConfig { SettingsRepairEnabled = repairEnabled });
    }

    [Fact]
    public void WindowRelativeGammaIsAnActionableDetectionButNotAReadySuccess()
    {
        Assert.True(VmOperations.IsActionableReadyScreenDetection(
            VmOperations.ReadyScreenState.GammaCalibration));
        Assert.Equal(
            VmOperations.ReadyScreenState.GammaCalibration,
            VmOperations.ResolveBoundedWindowReadyDetection(
                VmOperations.ReadyScreenState.GammaCalibration,
                completedWithinBound: true));
        Assert.False(VmOperations.IsActionableReadyScreenDetection(
            VmOperations.ReadyScreenState.Unknown));
        Assert.Equal(
            VmOperations.ReadyScreenState.Unknown,
            VmOperations.ResolveBoundedWindowReadyDetection(
                VmOperations.ReadyScreenState.GammaCalibration,
                completedWithinBound: false));
    }

    [Fact]
    public void FailedImmediateSecondProbeClearsTheFirstSighting()
    {
        var vm = NewVmOperations();
        var now = DateTimeOffset.UtcNow;

        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now);
        Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());
        Assert.False(vm.IsGammaCalibrationIncidentConfirmed());

        // ConfirmGammaCalibrationWithImmediateSecondProbe records exactly this Unknown observation
        // when its independent second sample fails.
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.Unknown), now.AddSeconds(1));
        Assert.False(vm.ShouldSuppressMenuInputForSettingsReset());

        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now.AddSeconds(2));
        Assert.False(vm.IsGammaCalibrationIncidentConfirmed());
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now.AddSeconds(3));
        Assert.True(vm.NeedsDonorSettings());
    }

    [Fact]
    public void TwoSightingsOutsideTheIncidentWindowDoNotConfirmARepair()
    {
        var vm = NewVmOperations();
        var now = DateTimeOffset.UtcNow;

        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now);
        vm.RecordObservedFrame(
            nameof(VmOperations.ReadyScreenState.GammaCalibration),
            now + VmOperations.GammaCalibrationIncidentWindow + TimeSpan.FromSeconds(1));

        Assert.False(vm.IsGammaCalibrationIncidentConfirmed());
        Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());
    }

    [Fact]
    public void LoneSightingCannotCombineWithOneAfterD2RRestarts()
    {
        var vm = NewVmOperations();
        var firstProcess = DateTimeOffset.UtcNow.AddMinutes(-2);
        var secondProcess = DateTimeOffset.UtcNow;

        vm.ReconcileGammaCalibrationProcessInstance(firstProcess);
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), firstProcess.AddSeconds(5));
        vm.ReconcileGammaCalibrationProcessInstance(secondProcess);
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), secondProcess.AddSeconds(5));

        Assert.False(vm.IsGammaCalibrationIncidentConfirmed());
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), secondProcess.AddSeconds(6));
        Assert.True(vm.NeedsDonorSettings());
    }

    [Fact]
    public void ConfirmedIncidentRemainsActionableWhileStoppedUntilRepairOrHealthyRender()
    {
        var vm = NewVmOperations();
        var firstProcess = DateTimeOffset.UtcNow.AddMinutes(-2);
        var secondProcess = DateTimeOffset.UtcNow;

        vm.ReconcileGammaCalibrationProcessInstance(firstProcess);
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), firstProcess.AddSeconds(5));
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), firstProcess.AddSeconds(6));
        Assert.True(vm.NeedsDonorSettings());

        // A quit, process-only fallback, cancellation, or relaunch cannot fix Settings.json.
        vm.RecordObservedFrame(nameof(VmOperations.VisibleD2RState.NotRunning), firstProcess.AddSeconds(7));
        vm.RecordObservedFrame(nameof(VmOperations.VisibleD2RState.Unknown), firstProcess.AddSeconds(8));
        vm.ReconcileGammaCalibrationProcessInstance(secondProcess);
        Assert.True(vm.NeedsDonorSettings());
        Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());

        // Reaching a real post-startup screen proves that the broken incident is over.
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.CharacterMenu), secondProcess.AddSeconds(5));
        Assert.False(vm.NeedsDonorSettings());
        Assert.False(vm.ShouldSuppressMenuInputForSettingsReset());
    }

    [Fact]
    public void DisabledRepairNeverRequestsASettingsWriteButStillSuppressesUnsafeInput()
    {
        var vm = NewVmOperations(repairEnabled: false);
        var now = DateTimeOffset.UtcNow;

        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now);
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now.AddSeconds(1));

        Assert.True(vm.IsGammaCalibrationIncidentConfirmed());
        Assert.False(vm.NeedsDonorSettings());
        Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());
    }

    [Fact]
    public async Task AgentRestartRetainsReadyLatchAndIncidentAttemptCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-ready-latch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var config = RepairConfig(settingsPath);
            var vm = new VmOperations(config);
            ConfirmGamma(vm);
            var repair = await ApplyRepairAsync(vm);

            Assert.True(repair.Ok, repair.Message);
            Assert.True(File.Exists(D2RSettingsFile.ResolveRepairStatePath(settingsPath)));

            var restarted = new VmOperations(config);
            var status = JsonSerializer.SerializeToElement(await restarted.GetStatusAsync(CancellationToken.None));
            var repairStatus = status.GetProperty("d2rSettingsRepair");
            Assert.True(repairStatus.GetProperty("needsReadyAfterSettingsRepair").GetBoolean());
            Assert.Equal("Ready", repairStatus.GetProperty("journalPhase").GetString());
            Assert.Equal(1, repairStatus.GetProperty("incidentRepairAttempts").GetInt32());
            Assert.Equal(JsonValueKind.String, repairStatus.GetProperty("incidentFirstRepairUtc").ValueKind);
            Assert.Equal(JsonValueKind.String, repairStatus.GetProperty("incidentLastRepairUtc").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmedPostCopyGammaDurablyReopensPreparedWithoutChargingBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-ready-gamma-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var firstAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
            var lastAttemptUtc = firstAttemptUtc.AddSeconds(5);
            var ready = new D2RSettingsRepairState(
                D2RSettingsRepairState.CurrentSchemaVersion,
                D2RSettingsRepairPhase.Ready,
                1,
                firstAttemptUtc,
                lastAttemptUtc);
            Assert.True(D2RSettingsFile.TryWriteRepairState(settingsPath, ready, out var writeError), writeError);

            var vm = new VmOperations(RepairConfig(settingsPath));
            var observedUtc = DateTimeOffset.UtcNow;
            vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), observedUtc);
            vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), observedUtc.AddSeconds(1));

            Assert.True(vm.NeedsDonorSettings());
            Assert.True(D2RSettingsFile.TryReadRepairState(settingsPath, out var prepared, out var readError), readError);
            Assert.Equal(D2RSettingsRepairPhase.Prepared, prepared.Phase);
            Assert.Equal(1, prepared.AttemptCount);
            Assert.Equal(firstAttemptUtc, prepared.FirstAttemptUtc);
            Assert.Equal(lastAttemptUtc, prepared.LastAttemptUtc);

            var status = JsonSerializer.SerializeToElement(await vm.GetStatusAsync(CancellationToken.None));
            var repairStatus = status.GetProperty("d2rSettingsRepair");
            Assert.True(repairStatus.GetProperty("needsDonorSettings").GetBoolean());
            Assert.False(repairStatus.GetProperty("needsReadyAfterSettingsRepair").GetBoolean());
            Assert.Equal("Prepared", repairStatus.GetProperty("journalPhase").GetString());
            Assert.Equal(1, repairStatus.GetProperty("incidentRepairAttempts").GetInt32());

            var restarted = new VmOperations(RepairConfig(settingsPath));
            Assert.True(restarted.NeedsDonorSettings());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthyRenderedFrameClearsPersistedRepairIncident()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-ready-clear-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var config = RepairConfig(settingsPath);
            var vm = new VmOperations(config);
            ConfirmGamma(vm);
            var repair = await ApplyRepairAsync(vm);
            Assert.True(repair.Ok, repair.Message);

            var statePath = D2RSettingsFile.ResolveRepairStatePath(settingsPath);
            Assert.True(File.Exists(statePath));

            vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.CharacterMenu));

            Assert.False(File.Exists(statePath));
            var restarted = new VmOperations(config);
            var status = JsonSerializer.SerializeToElement(await restarted.GetStatusAsync(CancellationToken.None));
            Assert.Equal(JsonValueKind.Null, status.GetProperty("d2rSettingsRepair").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(D2RSettingsRepairPhase.Prepared)]
    [InlineData(D2RSettingsRepairPhase.Ready)]
    public async Task ExpiredIncidentBudgetIsClearedWithoutLosingPhase(
        D2RSettingsRepairPhase phase)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-ready-expiry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var now = DateTimeOffset.UtcNow;
            var oldState = new D2RSettingsRepairState(
                D2RSettingsRepairState.CurrentSchemaVersion,
                phase,
                D2RSettingsRepairState.MaxAttemptsPerIncident,
                now - D2RSettingsRepairState.IncidentWindow - TimeSpan.FromMinutes(2),
                now - D2RSettingsRepairState.IncidentWindow - TimeSpan.FromMinutes(1));
            Assert.True(D2RSettingsFile.TryWriteRepairState(settingsPath, oldState, out var writeError), writeError);

            var restarted = new VmOperations(RepairConfig(settingsPath));
            var status = JsonSerializer.SerializeToElement(await restarted.GetStatusAsync(CancellationToken.None));
            var repairStatus = status.GetProperty("d2rSettingsRepair");

            Assert.Equal(phase.ToString(), repairStatus.GetProperty("journalPhase").GetString());
            Assert.Equal(
                phase == D2RSettingsRepairPhase.Ready,
                repairStatus.GetProperty("needsReadyAfterSettingsRepair").GetBoolean());
            Assert.Equal(
                phase == D2RSettingsRepairPhase.Prepared,
                repairStatus.GetProperty("needsDonorSettings").GetBoolean());
            Assert.Equal(0, repairStatus.GetProperty("incidentRepairAttempts").GetInt32());
            Assert.Equal(JsonValueKind.Null, repairStatus.GetProperty("incidentFirstRepairUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, repairStatus.GetProperty("incidentLastRepairUtc").ValueKind);
            Assert.True(D2RSettingsFile.TryReadRepairState(settingsPath, out var rewritten, out var readError), readError);
            Assert.Equal(phase, rewritten.Phase);
            Assert.Equal(0, rewritten.AttemptCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedJournalSurvivesFailedReplacementAndAuthorizesRestartRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-prepared-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            // A directory at the file path makes the donor rename fail after PREPARED is durable,
            // without relying on platform-specific permissions or timing.
            Directory.CreateDirectory(settingsPath);
            var config = RepairConfig(settingsPath);
            var vm = new VmOperations(config);
            ConfirmGamma(vm);

            var failed = await ApplyRepairAsync(vm);

            Assert.False(failed.Ok);
            var failedData = JsonSerializer.SerializeToElement(failed.Data);
            Assert.True(failedData.GetProperty("settingsRepairAttemptCharged").GetBoolean());
            Assert.Equal(1, failedData.GetProperty("incidentRepairAttempts").GetInt32());
            Assert.True(D2RSettingsFile.TryReadRepairState(settingsPath, out var prepared, out var readError), readError);
            Assert.Equal(D2RSettingsRepairPhase.Prepared, prepared.Phase);
            Assert.Equal(1, prepared.AttemptCount);

            var restarted = new VmOperations(config);
            Assert.True(restarted.NeedsDonorSettings());
            Assert.True(restarted.ShouldSuppressMenuInputForSettingsReset());

            Directory.Delete(settingsPath);
            File.WriteAllText(settingsPath, "{}");
            var retried = await ApplyRepairAsync(restarted);

            Assert.True(retried.Ok, retried.Message);
            var retriedData = JsonSerializer.SerializeToElement(retried.Data);
            Assert.True(retriedData.GetProperty("settingsRepairAttemptCharged").GetBoolean());
            Assert.Equal(2, retriedData.GetProperty("incidentRepairAttempts").GetInt32());
            Assert.True(D2RSettingsFile.TryReadRepairState(settingsPath, out var ready, out readError), readError);
            Assert.Equal(D2RSettingsRepairPhase.Ready, ready.Phase);
            Assert.Equal(2, ready.AttemptCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadyPhaseRefusesADuplicateReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-ready-duplicate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var vm = new VmOperations(RepairConfig(settingsPath));
            ConfirmGamma(vm);

            var first = await ApplyRepairAsync(vm);
            var second = await ApplyRepairAsync(vm);

            Assert.True(first.Ok, first.Message);
            var firstData = JsonSerializer.SerializeToElement(first.Data);
            Assert.True(firstData.GetProperty("settingsRepairAttemptCharged").GetBoolean());
            Assert.Equal(1, firstData.GetProperty("incidentRepairAttempts").GetInt32());
            Assert.False(second.Ok);
            Assert.Null(second.Data);
            Assert.Contains("Ready phase", second.Message);
            Assert.Single(Directory.GetFiles(directory, $"{D2RSettingsFile.SettingsFileName}.*.bak"));
            Assert.True(D2RSettingsFile.TryReadRepairState(settingsPath, out var state, out var readError), readError);
            Assert.Equal(D2RSettingsRepairPhase.Ready, state.Phase);
            Assert.Equal(1, state.AttemptCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidJournalFailsClosedUntilOperatorRemovesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-invalid-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var statePath = D2RSettingsFile.ResolveRepairStatePath(settingsPath)!;
            File.WriteAllText(statePath, "{ definitely-not-valid-json");
            var vm = new VmOperations(RepairConfig(settingsPath));

            Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());
            Assert.False(vm.NeedsDonorSettings());
            var status = JsonSerializer.SerializeToElement(await vm.GetStatusAsync(CancellationToken.None));
            var repairStatus = status.GetProperty("d2rSettingsRepair");
            Assert.True(repairStatus.GetProperty("repairBlocked").GetBoolean());
            Assert.Contains("Could not read", repairStatus.GetProperty("repairJournalError").GetString());

            var refused = await ApplyRepairAsync(vm);

            Assert.False(refused.Ok);
            Assert.Contains("invalid or unreadable", refused.Message);
            Assert.Equal("{}", File.ReadAllText(settingsPath));
            Assert.Empty(Directory.GetFiles(directory, $"{D2RSettingsFile.SettingsFileName}.*.bak"));

            File.Delete(statePath);
            Assert.False(vm.ShouldSuppressMenuInputForSettingsReset());
            Assert.False(vm.NeedsDonorSettings());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidExplicitSettingsPathFailsClosedAndSuppressesMenuInput()
    {
        var vm = new VmOperations(RepairConfig("invalid\0settings-path"));

        Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());
        Assert.False(vm.NeedsDonorSettings());

        var status = JsonSerializer.SerializeToElement(await vm.GetStatusAsync(CancellationToken.None));
        var repairStatus = status.GetProperty("d2rSettingsRepair");
        Assert.True(repairStatus.GetProperty("repairBlocked").GetBoolean());
        Assert.Contains(
            "d2rSettingsPath",
            repairStatus.GetProperty("repairJournalError").GetString());

        var refused = await ApplyRepairAsync(vm);
        Assert.False(refused.Ok);
        Assert.Null(refused.Data);
        Assert.Contains("Could not resolve", refused.Message);
    }

    [Fact]
    public void HealthyRenderedFrameClearsPreparedJournalToo()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"d2rops-prepared-clear-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, D2RSettingsFile.SettingsFileName);
            File.WriteAllText(settingsPath, "{}");
            var now = DateTimeOffset.UtcNow;
            Assert.True(D2RSettingsFile.TryWriteRepairState(
                settingsPath,
                new D2RSettingsRepairState(
                    D2RSettingsRepairState.CurrentSchemaVersion,
                    D2RSettingsRepairPhase.Prepared,
                    1,
                    now,
                    now),
                out var writeError), writeError);
            var vm = new VmOperations(RepairConfig(settingsPath));
            Assert.True(vm.ShouldSuppressMenuInputForSettingsReset());

            vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.CharacterMenu));

            Assert.False(File.Exists(D2RSettingsFile.ResolveRepairStatePath(settingsPath)));
            Assert.False(vm.ShouldSuppressMenuInputForSettingsReset());
            Assert.False(vm.NeedsDonorSettings());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepairPreconditionsRequireFreshGammaForRunningClientAndLatchForStoppedClient()
    {
        static bool Allowed(
            bool running,
            bool freshGamma,
            bool confirmed,
            D2RSettingsRepairPhase? phase = null,
            bool blocked = false)
        {
            return VmOperations.CanApplySettingsRepair(
                running,
                freshGamma,
                confirmed,
                phase,
                blocked,
                out _);
        }

        Assert.True(Allowed(running: false, freshGamma: false, confirmed: true));
        Assert.True(Allowed(
            running: false,
            freshGamma: false,
            confirmed: false,
            phase: D2RSettingsRepairPhase.Prepared));
        Assert.False(Allowed(running: false, freshGamma: false, confirmed: false));

        Assert.True(Allowed(running: true, freshGamma: true, confirmed: true));
        Assert.True(Allowed(
            running: true,
            freshGamma: true,
            confirmed: false,
            phase: D2RSettingsRepairPhase.Prepared));
        Assert.False(Allowed(running: true, freshGamma: false, confirmed: true));
        Assert.False(Allowed(
            running: false,
            freshGamma: false,
            confirmed: true,
            phase: D2RSettingsRepairPhase.Ready));
        Assert.False(Allowed(
            running: false,
            freshGamma: false,
            confirmed: true,
            blocked: true));
    }

    [Fact]
    public void PersistedPreparedCannotDowngradeAnInMemoryReadyAfterJournalBlock()
    {
        Assert.False(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            D2RSettingsRepairPhase.Ready,
            journalRewritePending: true,
            wasBlocked: true,
            D2RSettingsRepairPhase.Prepared));
        Assert.False(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            D2RSettingsRepairPhase.Ready,
            journalRewritePending: false,
            wasBlocked: true,
            D2RSettingsRepairPhase.Prepared));

        Assert.True(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            D2RSettingsRepairPhase.Prepared,
            journalRewritePending: true,
            wasBlocked: true,
            D2RSettingsRepairPhase.Ready));
        Assert.True(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            inMemoryPhase: null,
            journalRewritePending: false,
            wasBlocked: false,
            D2RSettingsRepairPhase.Prepared));
        Assert.True(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            D2RSettingsRepairPhase.Ready,
            journalRewritePending: false,
            wasBlocked: true,
            D2RSettingsRepairPhase.Ready));
        Assert.False(VmOperations.ShouldAdoptPersistedSettingsRepairState(
            D2RSettingsRepairPhase.Ready,
            journalRewritePending: true,
            wasBlocked: true,
            D2RSettingsRepairPhase.Ready));
    }

    [Fact]
    public void SettingsRepairProcessAuthorizationRejectsEveryNewGeneration()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var authorized = new VmOperations.D2RProcessGeneration(4711, startedUtc);
        var sameGeneration = new VmOperations.D2RProcessGeneration(4711, startedUtc);
        var reusedPid = new VmOperations.D2RProcessGeneration(4711, startedUtc.AddTicks(1));
        var additionalProcess = new VmOperations.D2RProcessGeneration(4712, startedUtc);

        Assert.True(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [authorized],
            [sameGeneration]));
        Assert.True(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [authorized],
            []));
        Assert.True(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [],
            []));
        Assert.False(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [],
            [additionalProcess]));
        Assert.False(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [authorized],
            [reusedPid]));
        Assert.False(VmOperations.ContainsOnlyAuthorizedSettingsRepairProcesses(
            [authorized],
            [sameGeneration, additionalProcess]));
    }

    private static VmAgentConfig RepairConfig(string settingsPath) => new()
    {
        SettingsRepairEnabled = true,
        SettingsRepairSettleSeconds = 0,
        D2RSettingsPath = settingsPath
    };

    private static void ConfirmGamma(VmOperations vm)
    {
        var now = DateTimeOffset.UtcNow;
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now);
        vm.RecordObservedFrame(nameof(VmOperations.ReadyScreenState.GammaCalibration), now.AddSeconds(1));
    }

    private static Task<CommandResult> ApplyRepairAsync(VmOperations vm)
    {
        var donorContent = JsonSerializer.Serialize(new
        {
            Display = new { Gamma = 1.0 },
            Audio = new { Volume = 0.5 },
            KeyBindings = new string('x', 2500)
        });
        return vm.HandleCommandAsync(
            new CommandRequest(
                "repair-test",
                "settings_repair",
                JsonSerializer.SerializeToElement(new
                {
                    settingsContent = donorContent,
                    settingsSourceAgentId = "d2r-hc-01"
                })),
            CancellationToken.None);
    }
}
