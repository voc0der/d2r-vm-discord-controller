using System.Text.Json;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// One VM's Settings.json gets reset by D2R, that client stops on the first-run gamma screen, and
// the fix is a healthy fleet member's copy of the file. These pin the two decisions that carry
// risk: whether a client is corrupt enough to overwrite, and which client is healthy enough to
// copy from.
public sealed class SettingsRepairPolicyTests
{
    [Fact]
    public void ARepairIsRequestedOnlyWhenTheAgentConfirmsIt()
    {
        Assert.True(SettingsRepairPolicy.NeedsDonorSettings(
            StatusJson("GammaCalibration", detected: true, needsDonorSettings: true)));
        // Seen once but not yet confirmed: the agent wants a second look before anything
        // overwrites a live client's settings.
        Assert.False(SettingsRepairPolicy.NeedsDonorSettings(
            StatusJson("GammaCalibration", detected: true, needsDonorSettings: false)));
        Assert.False(SettingsRepairPolicy.NeedsDonorSettings(
            StatusJson("CharacterScreen", detected: false, needsDonorSettings: false)));
    }

    // Agents that predate this feature send no d2rSettingsRepair block at all. That has to read as
    // "nothing to repair", never as a reason to start overwriting files.
    [Fact]
    public void OlderAgentsAndUnreadableStatusNeverRequestARepair()
    {
        Assert.False(SettingsRepairPolicy.NeedsDonorSettings("{\"d2rRunning\":true}"));
        Assert.False(SettingsRepairPolicy.NeedsDonorSettings("not json"));
        Assert.False(SettingsRepairPolicy.NeedsDonorSettings(null));
        Assert.False(SettingsRepairPolicy.IsSettingsCorrupt("{\"d2rRunning\":true}"));
    }

    // The donor rule the whole repair rests on: only a client that demonstrably got to character
    // select or past it can donate. Anything stuck earlier is, as far as this is concerned, also
    // broken - including the corrupt VM itself, which spends its time on the way to the gamma
    // screen looking like an ordinary unrecognized frame.
    [Theory]
    [InlineData("InGame", "ReachedLobbyOrGame")]
    [InlineData("LobbyOrGame", "ReachedLobbyOrGame")]
    [InlineData("CharacterScreen", "ReachedCharacterScreen")]
    [InlineData("OfflineCharacterScreen", "Unusable")]
    [InlineData("DiabloSplash", "Unusable")]
    [InlineData("Unknown", "Unusable")]
    [InlineData("NotRunning", "Unusable")]
    [InlineData("GraphicsDeviceFailure", "Unusable")]
    [InlineData("GammaCalibration", "Unusable")]
    public void DonorHealthRequiresReachingCharacterSelect(string visibleState, string expected)
    {
        var health = SettingsRepairPolicy.ClassifyDonor(StatusJson(visibleState, detected: false, needsDonorSettings: false));
        Assert.Equal(expected, health.ToString());
    }

    [Fact]
    public void ACorruptClientIsNeverADonorEvenIfItLooksHealthyOtherwise()
    {
        // Both flags set on a status that also claims CharacterScreen: the corruption flag wins.
        Assert.Equal(
            "Unusable",
            SettingsRepairPolicy.ClassifyDonor(StatusJson("CharacterScreen", detected: true, needsDonorSettings: true)).ToString());
    }

    [Fact]
    public void DonorsAreOrderedByEvidenceThatTheirSettingsWork()
    {
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [
                Candidate("hc1", "CharacterScreen"),
                Candidate("hc3", "InGame"),
                Candidate("hc4", "LobbyOrGame")
            ],
            preferredDonorAccountKey: null);

        // Lobby/in-game clients got all the way past character select on their settings, so they
        // outrank a client that has only reached character select.
        Assert.Equal(["hc3", "hc4", "hc1"], order);
    }

    [Fact]
    public void TheBrokenAccountAndUnhealthyPeersAreExcluded()
    {
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [
                Candidate("hc1", "OfflineCharacterScreen"),
                Candidate("hc2", "GammaCalibration", detected: true),
                Candidate("hc3", "DiabloSplash"),
                Candidate("hc4", "NotRunning"),
                Candidate("hc5", "InGame", connected: false),
                Candidate("hc6", "LobbyOrGame")
            ],
            preferredDonorAccountKey: null);

        Assert.Equal(["hc6"], order);
    }

    [Fact]
    public void AConfiguredDonorGoesFirstWhenItIsEligible()
    {
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [Candidate("hc1", "CharacterScreen"), Candidate("hc3", "InGame")],
            preferredDonorAccountKey: "hc1");

        // hc1 only reached character select and hc3 is in a game, but the operator named hc1.
        Assert.Equal(["hc1", "hc3"], order);
    }

    // A configured donor that is offline or broken must not block the repair - the point is to get
    // the stuck client a working file, not to insist on one particular source of it.
    [Fact]
    public void AnIneligibleConfiguredDonorIsSkippedRatherThanBlocking()
    {
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [Candidate("hc1", "GammaCalibration", detected: true), Candidate("hc3", "InGame")],
            preferredDonorAccountKey: "hc1");

        Assert.Equal(["hc3"], order);
    }

    [Fact]
    public void NoEligibleDonorReturnsAnEmptyOrderRatherThanGuessing()
    {
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [Candidate("hc1", "Unknown"), Candidate("hc3", "NotRunning")],
            preferredDonorAccountKey: "hc1");

        Assert.Empty(order);
    }

    [Fact]
    public void StatusRetainedAcrossAReconnectCannotAuthorizeADonor()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var order = SettingsRepairPolicy.SelectDonorOrder(
            "hc2",
            [
                new SettingsDonorCandidate(
                    "hc1",
                    Connected: true,
                    StatusJson("InGame", detected: false, needsDonorSettings: false),
                    ConnectedAt: now,
                    StatusReceivedAt: null),
                Candidate("hc3", "CharacterScreen")
            ],
            preferredDonorAccountKey: "hc1");

        Assert.Equal(["hc3"], order);
    }

    [Fact]
    public void DestructiveTargetRequiresStatusFromItsCurrentConnection()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var cachedAfterReconnect = new AgentSnapshot(
            "d2r-hc-02", "vm", null, null, null, Connected: true,
            ConnectedAt: now, LastSeenAt: now, LastStatusJson: StatusJson("GammaCalibration", true, true),
            StatusReceivedAt: null);
        var current = cachedAfterReconnect with { StatusReceivedAt = now };

        Assert.False(DiscordBot.HasStatusFromCurrentAgentConnection(cachedAfterReconnect));
        Assert.True(DiscordBot.HasStatusFromCurrentAgentConnection(current));
    }

    [Fact]
    public void LeaseAcquisitionAndFailedDonorDiscoveryDoNotSpendAnAttempt()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);

        using (var first = tracker.TryAcquireRepair("hc2", start, out _))
        {
            Assert.NotNull(first);
            Assert.Equal(0, tracker.AttemptsFor("hc2"));
        }

        using var retry = tracker.TryAcquireRepair("hc2", start.AddMinutes(1), out _);
        Assert.NotNull(retry);
        Assert.Equal(0, tracker.AttemptsFor("hc2"));
    }

    [Fact]
    public void ReconnectOrPreconditionRejectionsDoNotSpendAHostAttempt()
    {
        var rejected = new CommandResultInfo(
            "d2r-hc-02",
            "command-1",
            Ok: false,
            Message: "agent reconnected before the command was sent",
            Data: null);

        Assert.False(DiscordBot.TryGetAgentChargedSettingsRepairAttempt(rejected, out var attempts));
        Assert.Null(attempts);
    }

    [Fact]
    public void WireShapedNullDataOnAnUnchargedFailureDoesNotThrowOrSpendAnAttempt()
    {
        using var document = JsonDocument.Parse("null");
        var rejected = new CommandResultInfo(
            "d2r-hc-02",
            "command-1",
            Ok: false,
            Message: "fresh Gamma authorization failed",
            Data: document.RootElement.Clone());

        Assert.False(DiscordBot.TryGetAgentChargedSettingsRepairAttempt(rejected, out var attempts));
        Assert.Null(attempts);
    }

    [Fact]
    public void DurableAgentAttemptMarkerCarriesTheAuthoritativeBudget()
    {
        using var document = JsonDocument.Parse(
            """{"settingsRepairAttemptCharged":true,"incidentRepairAttempts":2}""");
        var failedAfterPrepared = new CommandResultInfo(
            "d2r-hc-02",
            "command-2",
            Ok: false,
            Message: "copy failed after D2R stopped",
            Data: document.RootElement.Clone());

        Assert.True(DiscordBot.TryGetAgentChargedSettingsRepairAttempt(
            failedAfterPrepared,
            out var attempts));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void AgentConfirmedAttemptSurvivesAReadyHeartbeatRacingTheResult()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);
        using var lease = tracker.TryAcquireRepair("hc2", start, out _);
        Assert.NotNull(lease);

        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "NotRunning",
                detected: false,
                needsDonorSettings: false,
                needsReadyAfterSettingsRepair: true,
                incidentRepairAttempts: 1,
                incidentFirstRepairUtc: start,
                incidentLastRepairUtc: start.AddSeconds(1),
                agentTimeUtc: start.AddSeconds(1)),
            start.AddSeconds(1));
        lease!.RecordAgentChargedAttempt(start.AddSeconds(2), durableAttemptCount: 1);
        lease.RecordRepairApplied("hc1", start.AddSeconds(2));

        Assert.Equal(1, tracker.AttemptsFor("hc2"));
        Assert.Equal(SettingsRecoveryStage.ReadyRequired, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void ChargedFailureOverridesAStaleReadyHeartbeatThatRacedItsResult()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-2", start);
        using var lease = tracker.TryAcquireRepair("hc2", start, out _);
        Assert.NotNull(lease);

        tracker.RecordReadyNeeded("hc2", start.AddSeconds(1), "stale-ready");
        lease!.RecordAgentChargedAttempt(start.AddSeconds(2), durableAttemptCount: 2);

        Assert.Equal(2, tracker.AttemptsFor("hc2"));
        Assert.Equal(SettingsRecoveryStage.RepairRequired, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void StaleReadyHeartbeatAfterAChargedFailureCannotMaskThePendingRepair()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-2", start);
        using (var lease = tracker.TryAcquireRepair("hc2", start, out _))
        {
            Assert.NotNull(lease);
            lease!.RecordAgentChargedAttempt(start.AddSeconds(1), durableAttemptCount: 2);
        }

        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "NotRunning",
                detected: false,
                needsDonorSettings: false,
                needsReadyAfterSettingsRepair: true,
                incidentRepairAttempts: 1,
                incidentFirstRepairUtc: start.AddMinutes(-1),
                incidentLastRepairUtc: start.AddMinutes(-1),
                agentTimeUtc: start),
            start.AddSeconds(2));

        Assert.Equal(2, tracker.AttemptsFor("hc2"));
        Assert.Equal(SettingsRecoveryStage.RepairRequired, tracker.RecoveryFor("hc2").Stage);

        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "NotRunning",
                detected: false,
                needsDonorSettings: false,
                needsReadyAfterSettingsRepair: true,
                incidentRepairAttempts: 2,
                incidentFirstRepairUtc: start,
                incidentLastRepairUtc: start.AddSeconds(1),
                agentTimeUtc: start.AddSeconds(3)),
            start.AddSeconds(3));

        Assert.Equal(SettingsRecoveryStage.ReadyRequired, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void ReadyWithResetAgentBudgetIsAcceptedAfterTheHostIncidentExpires()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);
        using (var lease = tracker.TryAcquireRepair("hc2", start, out _))
        {
            Assert.NotNull(lease);
            lease!.RecordAgentChargedAttempt(start, durableAttemptCount: 1);
        }

        var afterExpiry = start + SettingsRepairTracker.IncidentWindow + TimeSpan.FromSeconds(1);
        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "NotRunning",
                detected: false,
                needsDonorSettings: false,
                needsReadyAfterSettingsRepair: true,
                incidentRepairAttempts: 0,
                agentTimeUtc: afterExpiry),
            afterExpiry);

        Assert.Equal(0, tracker.AttemptsFor("hc2"));
        Assert.Equal(SettingsRecoveryStage.ReadyRequired, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void RepairLeaseIsSingleFlightAcrossSweepAndFollowCallers()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);

        using var sweepLease = tracker.TryAcquireRepair("hc2", start, out _);
        var followLease = tracker.TryAcquireRepair("hc2", start, out var blocked);

        Assert.NotNull(sweepLease);
        Assert.Null(followLease);
        Assert.Contains("already in progress", blocked);
        Assert.True(tracker.IsRepairInFlight("hc2"));
    }

    // A client that keeps coming back corrupt must not turn into a repair loop that closes it and
    // rewrites its file every follow-auto cycle forever.
    [Fact]
    public void ActualCopyAttemptsAreRateLimitedPerAccount()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        SpendCopyAttempt(tracker, "hc2", start);
        SpendCopyAttempt(tracker, "hc2", start.AddMinutes(1));

        Assert.Null(tracker.TryAcquireRepair("hc2", start.AddMinutes(2), out var blocked));
        Assert.Contains("not trying again before", blocked);
        Assert.Equal(SettingsRepairTracker.MaxAttemptsPerIncident, tracker.AttemptsFor("hc2"));
    }

    [Fact]
    public void RateLimitsAreIndependentAndALapsedIncidentGetsAFreshBudget()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        SpendCopyAttempt(tracker, "hc2", start);
        SpendCopyAttempt(tracker, "hc2", start);
        Assert.Null(tracker.TryAcquireRepair("hc2", start, out _));

        tracker.RecordRepairNeeded("hc3", "gamma-1", start);
        using (var otherAccount = tracker.TryAcquireRepair("hc3", start, out _))
        {
            Assert.NotNull(otherAccount);
        }

        using var nextIncident = tracker.TryAcquireRepair(
            "hc2",
            start + SettingsRepairTracker.IncidentWindow,
            out _);
        Assert.NotNull(nextIncident);
    }

    [Fact]
    public void SuccessfulCopyWaitsForReadyWithoutCopyingAgain()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);
        using (var lease = tracker.TryAcquireRepair("hc2", start, out _))
        {
            Assert.NotNull(lease);
            lease!.RecordAttempt(start);
            lease.RecordRepairApplied("hc1", start.AddSeconds(1));
        }

        var recovery = tracker.RecoveryFor("hc2");
        Assert.Equal(SettingsRecoveryStage.ReadyRequired, recovery.Stage);
        Assert.Equal("hc1", recovery.DonorAccountKey);
        Assert.Null(tracker.TryAcquireRepair("hc2", start.AddSeconds(2), out var blocked));
        Assert.Contains("ready/relaunch", blocked);
    }

    [Fact]
    public void FreshMasterRecoversReadyStageFromAgentLatch()
    {
        var tracker = new SettingsRepairTracker();
        var observed = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var status = StatusJson(
            "NotRunning",
            detected: false,
            needsDonorSettings: false,
            needsReadyAfterSettingsRepair: true);

        Assert.True(SettingsRepairPolicy.NeedsReadyAfterRepair(status));
        tracker.ObserveCurrentStatus("hc2", status, observed);

        Assert.Equal(SettingsRecoveryStage.ReadyRequired, tracker.RecoveryFor("hc2").Stage);
        Assert.Null(tracker.TryAcquireRepair("hc2", observed, out var blocked));
        Assert.Contains("ready/relaunch", blocked);
    }

    [Fact]
    public void FreshMasterRehydratesAndEnforcesExhaustedAgentIncidentBudget()
    {
        var hostObserved = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        // Deliberately skewed from the host. Rehydration uses the event's age relative to the
        // agent's own status time, not an absolute cross-machine timestamp comparison.
        var agentNow = new DateTimeOffset(2031, 4, 3, 9, 0, 0, TimeSpan.Zero);
        var status = StatusJson(
            "GammaCalibration",
            detected: true,
            needsDonorSettings: true,
            incidentRepairAttempts: SettingsRepairTracker.MaxAttemptsPerIncident,
            incidentFirstRepairUtc: agentNow.AddMinutes(-2),
            incidentLastRepairUtc: agentNow.AddMinutes(-1),
            agentTimeUtc: agentNow);
        var tracker = new SettingsRepairTracker();

        tracker.ObserveCurrentStatus("hc2", status, hostObserved);

        Assert.Equal(SettingsRepairTracker.MaxAttemptsPerIncident, tracker.AttemptsFor("hc2"));
        Assert.Null(tracker.TryAcquireRepair("hc2", hostObserved, out var blocked));
        Assert.Contains("not trying again before", blocked);
    }

    [Fact]
    public void RepeatedStaleAgentEvidenceCannotSlideAnExpiredCooldownForward()
    {
        var firstHostObservation = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var agentStatusUtc = new DateTimeOffset(2031, 4, 3, 9, 0, 0, TimeSpan.Zero);
        var status = StatusJson(
            "GammaCalibration",
            detected: true,
            needsDonorSettings: true,
            incidentRepairAttempts: SettingsRepairTracker.MaxAttemptsPerIncident,
            incidentFirstRepairUtc: agentStatusUtc.AddMinutes(-29),
            incidentLastRepairUtc: agentStatusUtc.AddMinutes(-29),
            agentTimeUtc: agentStatusUtc);
        var tracker = new SettingsRepairTracker();
        tracker.ObserveCurrentStatus("hc2", status, firstHostObservation);
        Assert.Equal(SettingsRepairTracker.MaxAttemptsPerIncident, tracker.AttemptsFor("hc2"));

        // The exact same cached payload is observed after the originally rebased attempt has
        // expired. It must not be anchored to this newer host time and block for another window.
        var afterExpiry = firstHostObservation.AddMinutes(2);
        tracker.ObserveCurrentStatus("hc2", status, afterExpiry);
        using var lease = tracker.TryAcquireRepair("hc2", afterExpiry, out var blocked);

        Assert.NotNull(lease);
        Assert.Equal("", blocked);
        Assert.Equal(0, tracker.AttemptsFor("hc2"));
    }

    [Fact]
    public void FailedOrCancelledCopyRemainsPendingAfterD2RStops()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);
        using (var lease = tracker.TryAcquireRepair("hc2", start, out _))
        {
            lease!.RecordAttempt(start);
            // The command failed after quit; no RecordRepairApplied call.
        }

        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson("NotRunning", detected: false, needsDonorSettings: false),
            start.AddMinutes(1));

        Assert.Equal(SettingsRecoveryStage.RepairRequired, tracker.RecoveryFor("hc2").Stage);
        Assert.NotNull(tracker.TryAcquireRepair("hc2", start.AddMinutes(1), out _));
    }

    [Fact]
    public void GammaEvidenceUsesIdentityNotCrossMachineClockOrdering()
    {
        var tracker = new SettingsRepairTracker();
        var hostDetected = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var hostApplied = hostDetected.AddMinutes(1);
        // Deliberately far ahead of the host: this timestamp came from the VM and is an opaque
        // identity only. It must never be ordered against hostApplied.
        var guestFirstSeen = new DateTimeOffset(2031, 4, 3, 9, 0, 0, TimeSpan.Zero);
        var preCopyStatus = StatusJson("GammaCalibration", true, true, lastSeenUtc: guestFirstSeen);
        tracker.ObserveCurrentStatus("hc2", preCopyStatus, hostDetected);
        using (var lease = tracker.TryAcquireRepair("hc2", hostDetected, out _))
        {
            lease!.RecordAttempt(hostDetected);
            lease.RecordRepairApplied("hc1", hostApplied);
        }

        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "GammaCalibration",
                true,
                true,
                lastSeenUtc: guestFirstSeen.AddMinutes(10),
                firstSeenUtc: guestFirstSeen),
            hostApplied.AddSeconds(1));
        Assert.Equal(SettingsRecoveryStage.ReadyRequired, tracker.RecoveryFor("hc2").Stage);

        // Even a clock that moved backwards is new evidence when its identity changes.
        var postCopyGamma = guestFirstSeen.AddHours(-1);
        tracker.ObserveCurrentStatus(
            "hc2",
            StatusJson(
                "GammaCalibration",
                true,
                true,
                lastSeenUtc: postCopyGamma,
                firstSeenUtc: postCopyGamma),
            hostApplied.AddMinutes(1));
        Assert.Equal(SettingsRecoveryStage.RepairRequired, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void RecoveryBeforeCopyRevokesAnActiveDonorExportLease()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        tracker.RecordRepairNeeded("hc2", "gamma-1", start);
        using var lease = tracker.TryAcquireRepair("hc2", start, out _);

        tracker.RecordRecovered("hc2");

        var error = Assert.Throws<InvalidOperationException>(() => lease!.RecordAttempt(start));
        Assert.Contains("demonstrated recovery", error.Message);
        Assert.Equal(0, tracker.AttemptsFor("hc2"));
        Assert.Equal(SettingsRecoveryStage.None, tracker.RecoveryFor("hc2").Stage);
    }

    [Fact]
    public void ARecoveredClientGetsAFreshBudgetImmediately()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        SpendCopyAttempt(tracker, "hc2", start);
        SpendCopyAttempt(tracker, "hc2", start.AddSeconds(1));
        tracker.RecordRecovered("hc2");

        Assert.Equal(0, tracker.AttemptsFor("hc2"));
        tracker.RecordRepairNeeded("hc2", "gamma-2", start.AddMinutes(1));
        Assert.NotNull(tracker.TryAcquireRepair("hc2", start.AddMinutes(1), out _));
    }

    [Fact]
    public void IdenticalCachedGammaEvidenceCannotReopenARecoveredIncident()
    {
        var tracker = new SettingsRepairTracker();
        var observed = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var firstSeen = new DateTimeOffset(2031, 4, 3, 9, 0, 0, TimeSpan.Zero);
        var cachedGamma = StatusJson(
            "GammaCalibration",
            detected: true,
            needsDonorSettings: true,
            lastSeenUtc: firstSeen,
            firstSeenUtc: firstSeen,
            incidentRepairAttempts: 1,
            incidentFirstRepairUtc: firstSeen,
            incidentLastRepairUtc: firstSeen,
            agentTimeUtc: firstSeen);
        tracker.ObserveCurrentStatus("hc2", cachedGamma, observed);
        using (var lease = tracker.TryAcquireRepair("hc2", observed, out _))
        {
            lease!.RecordAttempt(observed);
            lease.RecordRepairApplied("hc1", observed.AddSeconds(1));
        }

        tracker.RecordRecovered("hc2");
        tracker.ObserveCurrentStatus("hc2", cachedGamma, observed.AddSeconds(2));

        Assert.Equal(SettingsRecoveryStage.None, tracker.RecoveryFor("hc2").Stage);
        Assert.Equal(0, tracker.AttemptsFor("hc2"));
        Assert.Null(tracker.TryAcquireRepair("hc2", observed.AddSeconds(2), out var blocked));
        Assert.Contains("no confirmed", blocked);
    }

    private static void SpendCopyAttempt(
        SettingsRepairTracker tracker,
        string accountKey,
        DateTimeOffset attemptedUtc)
    {
        tracker.RecordRepairNeeded(accountKey, $"gamma-{attemptedUtc:O}", attemptedUtc);
        using var lease = tracker.TryAcquireRepair(accountKey, attemptedUtc, out var blocked);
        Assert.True(lease is not null, blocked);
        lease!.RecordAttempt(attemptedUtc);
    }

    private static SettingsDonorCandidate Candidate(
        string accountKey,
        string visibleState,
        bool detected = false,
        bool connected = true)
    {
        return new SettingsDonorCandidate(
            accountKey,
            connected,
            StatusJson(visibleState, detected, needsDonorSettings: detected),
            ConnectedAt: connected ? DateTimeOffset.UtcNow : null,
            StatusReceivedAt: connected ? DateTimeOffset.UtcNow : null);
    }

    private static string StatusJson(
        string visibleState,
        bool detected,
        bool needsDonorSettings,
        DateTimeOffset? lastSeenUtc = null,
        DateTimeOffset? firstSeenUtc = null,
        bool needsReadyAfterSettingsRepair = false,
        int incidentRepairAttempts = 0,
        DateTimeOffset? incidentFirstRepairUtc = null,
        DateTimeOffset? incidentLastRepairUtc = null,
        DateTimeOffset? agentTimeUtc = null)
    {
        return JsonSerializer.Serialize(new
        {
            d2rRunning = true,
            d2rVisibleState = visibleState,
            d2rSettingsRepair = new
            {
                detected,
                state = detected ? "GammaCalibration" : "None",
                needsDonorSettings,
                sightings = detected ? 2 : 0,
                firstSeenUtc = firstSeenUtc ?? lastSeenUtc,
                lastSeenUtc,
                needsReadyAfterSettingsRepair,
                incidentRepairAttempts,
                incidentFirstRepairUtc,
                incidentLastRepairUtc
            },
            timeUtc = agentTimeUtc
        });
    }
}
