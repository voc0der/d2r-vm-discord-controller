namespace AgentCommon;

public abstract class AgentConfig
{
    public string AgentId { get; set; } = "";
    public string ControllerUrl { get; set; } = "";
    public string SharedSecret { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 15;
}

public sealed class VmAgentConfig : AgentConfig
{
    public string? BattleNetPath { get; set; } = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    public string? BattleNetArgs { get; set; } = "--exec=\"launch OSI\"";
    public string BattleNetProcessName { get; set; } = "Battle.net";
    public string[] BattleNetProcessNames { get; set; } = ["Battle.net", "Battle.net Launcher", "Battle.net Helper"];
    public bool PreferBattleNetExecLaunch { get; set; } = true;
    public int BattleNetExecRetryDelaySeconds { get; set; } = 12;
    public string? D2RPath { get; set; }
    public string? D2RArgs { get; set; }
    public string D2RProcessName { get; set; } = "D2R";
    public string[] D2RProcessNames { get; set; } = ["D2R"];
    public string? WorkingDirectory { get; set; }
    public int LaunchGraceSeconds { get; set; } = 10;
    public int D2RStartTimeoutSeconds { get; set; } = 60;
    public bool IdleQuitEnabled { get; set; } = true;
    public int IdleQuitMinutes { get; set; } = 30;
    public int IdleQuitCheckSeconds { get; set; } = 60;
    // A client that crashes or freezes on the black-surround game-load screen never resolves
    // on its own, so once the load screen's black surround has been confirmed on watchdog
    // ticks spanning this many minutes, the agent quits D2R so the next ready cycle can
    // relaunch it. Deliberately keyed on the surround pixels alone - never on how the screen
    // classifies, since brighter load-screen artwork frames can cross the splash detector's
    // thresholds. The default has to clear the documented legitimate black stretches
    // (intro cinematic frames, post-intro RAM/VRAM load) with room to spare - raise it
    // before lowering it.
    public bool StuckLoadScreenQuitEnabled { get; set; } = true;
    public int StuckLoadScreenQuitMinutes { get; set; } = 4;
    public bool PartyMemberCountEnabled { get; set; } = true;
    public int PartyMemberCountIntervalSeconds { get; set; } = 30;
    public string PowerShellPath { get; set; } = "powershell.exe";
    public int ScreenshotTimeoutSeconds { get; set; } = 30;
    public D2RUiAutomationConfig Ui { get; set; } = new();
}

public sealed class D2RUiAutomationConfig
{
    public int StepDelayMs { get; set; } = 350;
    public int LongDelayMs { get; set; } = 1500;
    public int GameLoadSeconds { get; set; } = 6;
    public bool ToggleLegacyGraphicsAfterEnteringGame { get; set; } = true;
    public int LegacyGraphicsToggleDelaySeconds { get; set; } = 20;
    public int LobbyLoadSeconds { get; set; } = 3;
    public int ReadyStartupSkipSeconds { get; set; } = 45;
    public int ReadyStartupSkipIntervalMs { get; set; } = 100;
    public int ReadyStartupBlindSuccessSeconds { get; set; } = 0;
    public int ReadyNudgeMinDelayMs { get; set; } = 1000;
    public int ReadyNudgeMaxDelayMs { get; set; } = 2000;
    public int WindowFocusTimeoutSeconds { get; set; } = 30;
    public int CharacterScreenSettleSeconds { get; set; } = 1;
    public int CharacterScreenReadyTimeoutSeconds { get; set; } = 45;
    public int LobbyReadyTimeoutSeconds { get; set; } = 30;
    public int GameEntryStartTimeoutSeconds { get; set; } = 30;
    public int DefaultCharacterSlot { get; set; } = 1;
    public int DefaultFriendRow { get; set; } = 1;
    public double FriendRowHeight { get; set; } = 0.049;
    // Sub-region within a friend row used for the follow-bind fingerprint. Keep the left edge
    // on the visible name text and extend rightward for enough letters to avoid false positives
    // from visually similar short names; avoid the status line below it (which changes
    // constantly: "In Menus" vs "Act I, Hell" vs "Offline").
    //
    // These were measured, not estimated, against lobby_friends_list.png (see
    // FollowFingerprintReferenceTests). In that capture a friend row's name text occupies roughly
    // rowY-14 to rowY-4 px at 768p and the status line sits ~8px below it. The original band
    // (offsetY -0.010, height 0.022) was centered between the two and sampled BOTH: with 4 grid
    // rows over ~17px, one to two of every four sample rows landed on the status line. That is
    // why follow-auto drifted - "In Menus" vs "Act I Hell" changes the fingerprint of a friend
    // who has not moved, and it is text most rows share, which pulls their scores together.
    // Excluding it took the closest rival from sig 50 to sig 92+ on the same capture.
    public double FriendRowFingerprintOffsetX { get; set; } = 0.000;
    public double FriendRowFingerprintOffsetY { get; set; } = -0.0153;
    public double FriendRowFingerprintWidthRatio { get; set; } = 0.160;
    public double FriendRowFingerprintHeightRatio { get; set; } = 0.016;
    // Density chosen by sweeping the reference capture and scoring each candidate on its WORST
    // result across +/-2px of vertical drift, not its best at one exact alignment - a grid that
    // only separates cleanly when perfectly aligned is a grid that fails intermittently on live
    // VMs. 48x12 was the first that kept every rival row outside the match gate at every offset
    // in that range; 64x6 and 64x12 both let a row back inside at some alignments.
    //
    // Widening the band instead was considered and rejected: dark-on-dark grid points are skipped
    // by the comparison entirely so extra blank space adds no discrimination, and reaching further
    // out on either side starts capturing the friends panel's own bright chrome - identical on
    // every row, so it pulls their scores together. Extra grid points are cheap since v0.2.93:
    // each region is one BitBlt and every sample is read from the local bitmap, not the desktop.
    public int FriendRowFingerprintGridColumns { get; set; } = 48;
    public int FriendRowFingerprintGridRows { get; set; } = 12;
    public int FriendRowFingerprintMaxScanRows { get; set; } = 8;
    public bool ClickBattleNetPlayWhenNeeded { get; set; } = true;
    public bool DismissBattleNetWhatsNewWhenNeeded { get; set; } = true;
    public UiPoint BattleNetPlayButton { get; set; } = new(0.129, 0.703);
    public UiPoint BattleNetWhatsNewTitle { get; set; } = new(0.226, 0.187);
    public UiPoint BattleNetWhatsNewCloseButton { get; set; } = new(0.843, 0.146);
    public UiPoint IntroSkipPoint { get; set; } = new(0.500, 0.500);
    public int IntroClickCount { get; set; } = 80;
    public int IntroClickDelayMs { get; set; } = 250;
    public int TitleScreenKeyPressCount { get; set; } = 6;
    public int TitleScreenKeyPressDelayMs { get; set; } = 500;
    public UiPoint[] CharacterSlots { get; set; } =
    [
        new(0.890, 0.120),
        new(0.890, 0.210),
        new(0.890, 0.300),
        new(0.890, 0.380),
        new(0.890, 0.470),
        new(0.890, 0.560),
        new(0.890, 0.650),
        new(0.890, 0.740)
    ];
    public UiPoint CharacterPlayButton { get; set; } = new(0.420, 0.897);
    public UiPoint CharacterLobbyButton { get; set; } = new(0.585, 0.897);
    public UiPoint CharacterOnlineTab { get; set; } = new(0.850, 0.049);
    public UiPoint LobbyPartyIcon { get; set; } = new(0.096, 0.707);
    public UiPoint FriendsAccordionHeader { get; set; } = new(0.180, 0.139);
    public UiPoint FriendRowStart { get; set; } = new(0.180, 0.180);
    public UiPoint FriendContextJoinGame { get; set; } = new(0.278, 0.344);
    public UiPoint JoinGameTab { get; set; } = new(0.766, 0.071);
    public UiPoint JoinGameNameField { get; set; } = new(0.697, 0.138);
    public UiPoint JoinPasswordField { get; set; } = new(0.837, 0.138);
    public UiPoint JoinDifficultyDropdown { get; set; } = new(0.844, 0.191);
    public UiPoint JoinDifficultyNormalOption { get; set; } = new(0.844, 0.220);
    public UiPoint JoinDifficultyNightmareOption { get; set; } = new(0.844, 0.255);
    public UiPoint JoinDifficultyHellOption { get; set; } = new(0.844, 0.290);
    public UiPoint JoinGameButton { get; set; } = new(0.765, 0.622);
    public UiPoint GameEntryErrorDialogOkButton { get; set; } = new(0.500, 0.539);
    public UiPoint CannotJoinCurrentCharacterCancelButton { get; set; } = new(0.425, 0.539);
    public UiPoint CreateGameTab { get; set; } = new(0.673, 0.071);
    public UiPoint CreateGameNameField { get; set; } = new(0.766, 0.160);
    public UiPoint CreatePasswordField { get; set; } = new(0.766, 0.224);
    public UiPoint CreateNormalButton { get; set; } = new(0.697, 0.350);
    public UiPoint CreateNightmareButton { get; set; } = new(0.767, 0.350);
    public UiPoint CreateHellButton { get; set; } = new(0.832, 0.350);
    public UiPoint CreateGameButton { get; set; } = new(0.765, 0.619);
    public UiPoint SaveAndExitButton { get; set; } = new(0.500, 0.439);
    public UiPoint ModernHealthGlobe { get; set; } = new(0.260, 0.900);
    public UiPoint ModernManaGlobe { get; set; } = new(0.760, 0.900);
    public UiPoint LegacyHealthGlobe { get; set; } = new(0.200, 0.900);
    public UiPoint LegacyManaGlobe { get; set; } = new(0.800, 0.900);
    public UiPoint InGameHudBar { get; set; } = new(0.500, 0.955);
}

public sealed class UiPoint
{
    public UiPoint()
    {
    }

    public UiPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; set; }
    public double Y { get; set; }
}
