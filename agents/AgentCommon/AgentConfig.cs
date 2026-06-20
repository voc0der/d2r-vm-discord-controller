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
    public bool PreferBattleNetExecLaunch { get; set; } = true;
    public int BattleNetExecRetryDelaySeconds { get; set; } = 12;
    public string? D2RPath { get; set; }
    public string? D2RArgs { get; set; }
    public string D2RProcessName { get; set; } = "D2R";
    public string? WorkingDirectory { get; set; }
    public int LaunchGraceSeconds { get; set; } = 10;
    public int D2RStartTimeoutSeconds { get; set; } = 90;
    public bool IdleQuitEnabled { get; set; } = true;
    public int IdleQuitMinutes { get; set; } = 30;
    public int IdleQuitCheckSeconds { get; set; } = 60;
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
    public int ReadyNudgeMinDelayMs { get; set; } = 2000;
    public int ReadyNudgeMaxDelayMs { get; set; } = 4000;
    public int WindowFocusTimeoutSeconds { get; set; } = 30;
    public int CharacterScreenSettleSeconds { get; set; } = 1;
    public int CharacterScreenReadyTimeoutSeconds { get; set; } = 75;
    public int LobbyReadyTimeoutSeconds { get; set; } = 30;
    public int GameEntryStartTimeoutSeconds { get; set; } = 30;
    public int DefaultCharacterSlot { get; set; } = 1;
    public int DefaultFriendRow { get; set; } = 1;
    public double FriendRowHeight { get; set; } = 0.049;
    public bool ClickBattleNetPlayWhenNeeded { get; set; } = true;
    public UiPoint BattleNetPlayButton { get; set; } = new(0.129, 0.703);
    public UiPoint IntroSkipPoint { get; set; } = new(0.500, 0.500);
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
    public UiPoint LobbyPartyIcon { get; set; } = new(0.096, 0.707);
    public UiPoint FriendRowStart { get; set; } = new(0.180, 0.180);
    public UiPoint FriendContextJoinGame { get; set; } = new(0.318, 0.354);
    public UiPoint JoinGameTab { get; set; } = new(0.766, 0.071);
    public UiPoint JoinGameNameField { get; set; } = new(0.697, 0.138);
    public UiPoint JoinPasswordField { get; set; } = new(0.837, 0.138);
    public UiPoint JoinDifficultyDropdown { get; set; } = new(0.844, 0.191);
    public UiPoint JoinDifficultyNormalOption { get; set; } = new(0.844, 0.220);
    public UiPoint JoinDifficultyNightmareOption { get; set; } = new(0.844, 0.255);
    public UiPoint JoinDifficultyHellOption { get; set; } = new(0.844, 0.290);
    public UiPoint JoinGameButton { get; set; } = new(0.765, 0.622);
    public UiPoint GameEntryErrorDialogOkButton { get; set; } = new(0.500, 0.539);
    public UiPoint CreateGameTab { get; set; } = new(0.673, 0.071);
    public UiPoint CreateGameNameField { get; set; } = new(0.766, 0.160);
    public UiPoint CreatePasswordField { get; set; } = new(0.766, 0.224);
    public UiPoint CreateNormalButton { get; set; } = new(0.697, 0.350);
    public UiPoint CreateNightmareButton { get; set; } = new(0.767, 0.350);
    public UiPoint CreateHellButton { get; set; } = new(0.832, 0.350);
    public UiPoint CreateGameButton { get; set; } = new(0.765, 0.619);
    public UiPoint SaveAndExitButton { get; set; } = new(0.500, 0.439);
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
