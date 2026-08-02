using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Every menu command runs under the agent's command gate, so the status a command attaches to its
// own result is always the process-only one. Whether a state the detector genuinely saw survives
// into that payload is decided by one lookup table - and a state missing from it is invisible to
// the host in exactly the situation the host most needs it.
//
// That is not hypothetical: GammaCalibration was missing here when the settings-repair feature
// first shipped, which made the host's automatic repair - whose only trigger is a failed
// menu_ready's status payload - impossible to fire. These tests exist so the next state added to
// the enum cannot repeat it.
public sealed class VisibleStateMappingTests
{
    [Fact]
    public void EveryVisibleStateSurvivesAProcessOnlyStatus()
    {
        var unmapped = Enum.GetValues<VmOperations.VisibleD2RState>()
            // Unknown is the one deliberate exception: it carries no information, so a process-only
            // status is better off guessing from the activity cache than reporting it verbatim.
            .Where(state => state != VmOperations.VisibleD2RState.Unknown)
            .Where(state => VmOperations.MapObservedFrameToVisibleState(state.ToString()) != state)
            .ToArray();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void TheGammaScreenSurvivesAProcessOnlyStatus()
    {
        Assert.Equal(
            VmOperations.VisibleD2RState.GammaCalibration,
            VmOperations.MapObservedFrameToVisibleState("GammaCalibration"));
    }

    // The ready loop records its own state names, which are not the same enum. A ready frame that
    // does not map falls back to activity guessing, so these have to be pinned too.
    [Theory]
    [InlineData("CharacterMenu", "CharacterScreen")]
    [InlineData("CharacterScreen", "CharacterScreen")]
    [InlineData("OfflineCharacterScreen", "OfflineCharacterScreen")]
    [InlineData("DiabloSplash", "DiabloSplash")]
    public void ReadyLoopFrameNamesMapToVisibleStates(string frame, string expected)
    {
        Assert.Equal(expected, VmOperations.MapObservedFrameToVisibleState(frame)?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ConnectingToBattleNet")]
    public void FramesWithNoMappingFallBack(string? frame)
    {
        Assert.Null(VmOperations.MapObservedFrameToVisibleState(frame));
    }

    // The pulse's own in-game verdict. Null means "no evidence" and the host never resyncs a bot on
    // it, so a startup prompt that is definitively not a game must return false, not null - a
    // client parked on the gamma screen would otherwise read as an ambiguous load screen forever.
    [Theory]
    [InlineData("InGame", true)]
    [InlineData("LobbyOrGame", false)]
    [InlineData("CharacterScreen", false)]
    [InlineData("OfflineCharacterScreen", false)]
    [InlineData("NotRunning", false)]
    [InlineData("GraphicsDeviceFailure", false)]
    [InlineData("GammaCalibration", false)]
    // Load screens and degraded captures stay ambiguous on purpose.
    [InlineData("DiabloSplash", null)]
    [InlineData("Unknown", null)]
    public void PulseInGameVerdictIsDeliberateForEveryState(string state, bool? expected)
    {
        Assert.Equal(
            expected,
            VmOperations.ClassifyPulseInGame(Enum.Parse<VmOperations.VisibleD2RState>(state)));
    }

    [Fact]
    public void EveryVisibleStateHasAPulseVerdictDecision()
    {
        // Not asserting a value - just that adding a state forces a look at this table, since the
        // default arm silently returns null ("no evidence") for anything unlisted.
        var states = Enum.GetValues<VmOperations.VisibleD2RState>();
        Assert.Equal(9, states.Length);
    }
}
