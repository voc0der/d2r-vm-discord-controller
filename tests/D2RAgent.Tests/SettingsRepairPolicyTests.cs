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

    // A client that keeps coming back corrupt must not turn into a repair loop that closes it and
    // rewrites its file every follow-auto cycle forever.
    [Fact]
    public void RepairsAreRateLimitedPerAccount()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc2", start.AddMinutes(1), out _));
        Assert.False(tracker.TryBeginRepair("hc2", start.AddMinutes(2), out var blocked));
        Assert.Contains("not trying again before", blocked);
        Assert.Equal(SettingsRepairTracker.MaxAttemptsPerIncident, tracker.AttemptsFor("hc2"));
    }

    [Fact]
    public void RateLimitsAreIndependentPerAccount()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.False(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc3", start, out _));
    }

    [Fact]
    public void ALapsedIncidentGetsAFreshBudget()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.False(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc2", start + SettingsRepairTracker.IncidentWindow, out _));
    }

    [Fact]
    public void ARecoveredClientGetsAFreshBudgetImmediately()
    {
        var tracker = new SettingsRepairTracker();
        var start = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        Assert.True(tracker.TryBeginRepair("hc2", start, out _));
        tracker.RecordRecovered("hc2");

        Assert.Equal(0, tracker.AttemptsFor("hc2"));
        Assert.True(tracker.TryBeginRepair("hc2", start.AddMinutes(1), out _));
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
            StatusJson(visibleState, detected, needsDonorSettings: detected));
    }

    private static string StatusJson(string visibleState, bool detected, bool needsDonorSettings)
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
                sightings = detected ? 2 : 0
            }
        });
    }
}
