using AgentCommon;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class DcloneParkTests
{
    [Fact]
    public void BotCountDefaultsToEveryOnlineAccount()
    {
        Assert.Equal(9, DcloneParkPolicy.ResolveBotCount(onlineAccounts: 9, requested: null));
    }

    [Fact]
    public void BotCountNeverExceedsTheOnlineAccounts()
    {
        Assert.Equal(3, DcloneParkPolicy.ResolveBotCount(onlineAccounts: 3, requested: 20));
    }

    [Fact]
    public void BotCountParksASubsetWhenAskedFor()
    {
        Assert.Equal(2, DcloneParkPolicy.ResolveBotCount(onlineAccounts: 7, requested: 2));
    }

    [Fact]
    public void BotCountIsZeroWithNoOnlineAccounts()
    {
        Assert.Equal(0, DcloneParkPolicy.ResolveBotCount(onlineAccounts: 0, requested: 4));
    }

    [Fact]
    public void OnlyTheInGameFrameCountsAsParked()
    {
        Assert.Equal(
            DcloneParkPresence.Parked,
            DcloneParkPolicy.Classify(connected: true, """{"d2rRunning":true,"d2rVisibleState":"InGame"}"""));
    }

    // These mirror VmOperations.ClassifyPulseInGame, which is the fleet's existing answer to
    // "is this client in a game". LobbyOrGame is deliberately a drop, not an unknown.
    [Theory]
    [InlineData("LobbyOrGame")]
    [InlineData("CharacterScreen")]
    [InlineData("OfflineCharacterScreen")]
    [InlineData("NotRunning")]
    [InlineData("GraphicsDeviceFailure")]
    [InlineData("GammaCalibration")]
    public void ScreensDToRCannotShowFromInsideAGameCountAsADrop(string visibleState)
    {
        Assert.Equal(
            DcloneParkPresence.OutOfGame,
            DcloneParkPolicy.Classify(connected: true, $$"""{"d2rRunning":true,"d2rVisibleState":"{{visibleState}}"}"""));
    }

    [Theory]
    [InlineData("DiabloSplash")]
    [InlineData("Unknown")]
    public void LoadScreensAndDegradedCapturesAreNotEvidence(string visibleState)
    {
        Assert.Equal(
            DcloneParkPresence.NoEvidence,
            DcloneParkPolicy.Classify(connected: true, $$"""{"d2rRunning":true,"d2rVisibleState":"{{visibleState}}"}"""));
    }

    [Fact]
    public void AStoppedClientIsADropEvenWithNoVisibleState()
    {
        Assert.Equal(
            DcloneParkPresence.OutOfGame,
            DcloneParkPolicy.Classify(connected: true, """{"d2rRunning":false}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadableStatusIsNotEvidence(string? statusJson)
    {
        Assert.Equal(DcloneParkPresence.NoEvidence, DcloneParkPolicy.Classify(connected: true, statusJson));
    }

    [Fact]
    public void ADisconnectedAgentIsOfflineRatherThanDropped()
    {
        Assert.Equal(
            DcloneParkPresence.Offline,
            DcloneParkPolicy.Classify(connected: false, """{"d2rRunning":true,"d2rVisibleState":"InGame"}"""));
    }

    [Fact]
    public void ALoadScreenBetweenTwoDropsDoesNotResetTheStreak()
    {
        var streak = DcloneParkPolicy.NextOutOfGameStreak(0, DcloneParkPresence.OutOfGame);
        streak = DcloneParkPolicy.NextOutOfGameStreak(streak, DcloneParkPresence.NoEvidence);
        streak = DcloneParkPolicy.NextOutOfGameStreak(streak, DcloneParkPresence.OutOfGame);

        Assert.Equal(2, streak);
    }

    [Fact]
    public void SeeingTheClientBackInGameClearsTheStreak()
    {
        var streak = DcloneParkPolicy.NextOutOfGameStreak(2, DcloneParkPresence.Parked);

        Assert.Equal(0, streak);
        Assert.False(DcloneParkPolicy.ShouldRepark(streak));
    }

    [Fact]
    public void OneOutOfGameFrameDoesNotTearDownAPark()
    {
        var run = NewRun();
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");

        Assert.False(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.OutOfGame, DateTimeOffset.UtcNow));
        Assert.Equal(DcloneSlotState.Parked, Slot(run, "hc1").State);
        Assert.Equal("ewk52we", Slot(run, "hc1").GameName);
    }

    [Fact]
    public void AConfirmedDropRebuildsTheParkUnderANewName()
    {
        var run = NewRun();
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");
        var nowUtc = DateTimeOffset.UtcNow;

        for (var read = 1; read < DcloneParkPolicy.ReparkAfterConsecutiveOutOfGameReads; read++)
        {
            Assert.False(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.OutOfGame, nowUtc));
        }

        Assert.True(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.OutOfGame, nowUtc));

        var slot = Slot(run, "hc1");
        Assert.Equal(DcloneSlotState.Reparking, slot.State);
        Assert.Equal(1, slot.Reparks);
        // The old name is dropped on sight: it is no longer joinable and must not stay on a
        // message people are copying credentials out of.
        Assert.Null(slot.GameName);
        Assert.Null(slot.Password);
    }

    [Fact]
    public void ASecondPollNeverStartsATwinRebuild()
    {
        var run = NewRun();
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");
        var nowUtc = DateTimeOffset.UtcNow;
        for (var read = 0; read < DcloneParkPolicy.ReparkAfterConsecutiveOutOfGameReads; read++)
        {
            run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.OutOfGame, nowUtc);
        }

        for (var read = 0; read < DcloneParkPolicy.ReparkAfterConsecutiveOutOfGameReads * 2; read++)
        {
            Assert.False(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.OutOfGame, nowUtc));
        }

        Assert.Equal(1, Slot(run, "hc1").Reparks);
    }

    [Fact]
    public void AnOfflineAgentIsReportedRatherThanRebuilt()
    {
        var run = NewRun();
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");
        var nowUtc = DateTimeOffset.UtcNow;

        for (var read = 0; read < DcloneParkPolicy.ReparkAfterConsecutiveOutOfGameReads * 2; read++)
        {
            Assert.False(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.Offline, nowUtc));
        }

        Assert.Equal(DcloneSlotState.Offline, Slot(run, "hc1").State);
    }

    [Fact]
    public void AnAgentThatComesBackStillInItsGameKeepsIt()
    {
        var run = NewRun();
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");
        var nowUtc = DateTimeOffset.UtcNow;
        run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.Offline, nowUtc);

        Assert.False(run.RegisterReadingAndTryBeginRepark("hc1", DcloneParkPresence.Parked, nowUtc));

        var slot = Slot(run, "hc1");
        Assert.Equal(DcloneSlotState.Parked, slot.State);
        Assert.Equal("ewk52we", slot.GameName);
    }

    [Fact]
    public void AFailedSlotIsLeftAloneUntilItsRetryIsDue()
    {
        var run = NewRun();
        var failedAt = DateTimeOffset.UtcNow;
        run.MarkFailed("hc1", "the client never entered the game", failedAt);

        Assert.False(run.RegisterReadingAndTryBeginRepark(
            "hc1",
            DcloneParkPresence.OutOfGame,
            failedAt + DcloneParkPolicy.FailedSlotRetryDelay - TimeSpan.FromSeconds(1)));
        Assert.Equal(DcloneSlotState.Failed, Slot(run, "hc1").State);
    }

    [Fact]
    public void AFailedSlotIsRetriedOnceItsBackoffElapses()
    {
        var run = NewRun();
        var failedAt = DateTimeOffset.UtcNow;
        run.MarkFailed("hc1", "the client never entered the game", failedAt);

        Assert.True(run.RegisterReadingAndTryBeginRepark(
            "hc1",
            DcloneParkPresence.OutOfGame,
            failedAt + DcloneParkPolicy.FailedSlotRetryDelay));
        Assert.Equal(DcloneSlotState.Reparking, Slot(run, "hc1").State);
    }

    [Fact]
    public void AParkAttemptIsOnlyArmedOnce()
    {
        var run = NewRun();

        Assert.True(run.TryBeginInitialPark("hc1"));
        Assert.False(run.TryBeginInitialPark("hc1"));

        run.EndParkAttempt("hc1");
        Assert.True(run.TryBeginInitialPark("hc1"));
    }

    [Fact]
    public void ARunNeverHandsOutTheSameGameNameTwice()
    {
        var run = NewRun();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 500; i++)
        {
            var (name, password) = run.MintCredentials();
            Assert.True(names.Add(name), $"Minted {name} twice in one run.");
            Assert.Equal(RandomGameCredentials.DcloneGameNameLength, name.Length);
            Assert.Equal(RandomGameCredentials.DclonePasswordLength, password.Length);
        }
    }

    // D2R caps classic-engine game names and passwords at 15 characters, and a name that gets
    // silently clipped by the client is a name nobody else can type in.
    [Fact]
    public void MintedCredentialsFitDiabloTwoFields()
    {
        var name = RandomGameCredentials.NewDcloneGameName();
        var password = RandomGameCredentials.NewDclonePassword();

        Assert.InRange(name.Length, 1, 15);
        Assert.InRange(password.Length, 1, 15);
        Assert.All(name + password, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Fact]
    public void SnapshotKeepsTheRosterInTheOrderItWasBuilt()
    {
        var run = new DcloneParkRun(1, "hell", DateTimeOffset.UtcNow);
        run.AddSlot("hc3", "hc3", "agent-3");
        run.AddSlot("hc1", "hc1", "agent-1");
        run.AddSlot("hc2", "hc2", "agent-2");

        Assert.Equal(new[] { "hc3", "hc1", "hc2" }, run.Snapshot().Select(slot => slot.AccountKey));
    }

    [Fact]
    public void ParkedCountOnlyCountsBotsActuallyHoldingAGame()
    {
        var run = new DcloneParkRun(1, "hell", DateTimeOffset.UtcNow);
        run.AddSlot("hc1", "hc1", "agent-1");
        run.AddSlot("hc2", "hc2", "agent-2");
        run.AddSlot("hc3", "hc3", "agent-3");
        run.MarkParked("hc1", "ewk52we", "q", "holding the game open");
        run.MarkPreparing("hc2", "warming up");
        run.MarkFailed("hc3", "ready failed", DateTimeOffset.UtcNow);

        Assert.Equal(1, run.ParkedCount());
        Assert.Equal(3, run.SlotCount());
    }

    [Fact]
    public void OnlyTheFirstStopReasonIsKept()
    {
        var run = NewRun();

        run.RecordStopReason("quit-all was called");
        run.RecordStopReason("leave was called for every account");

        Assert.Equal("quit-all was called", run.StopReason);
    }

    private static DcloneParkRun NewRun()
    {
        var run = new DcloneParkRun(1, "hell", DateTimeOffset.UtcNow);
        run.AddSlot("hc1", "hc1", "agent-1");
        return run;
    }

    private static DcloneParkSlotSnapshot Slot(DcloneParkRun run, string accountKey)
    {
        return run.Snapshot().Single(slot => slot.AccountKey == accountKey);
    }
}
