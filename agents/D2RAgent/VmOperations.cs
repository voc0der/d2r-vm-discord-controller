using System.Diagnostics;
using System.Text;
using AgentCommon;

namespace D2RAgent;

public sealed class VmOperations
{
    private const string DefaultBattleNetPath = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string DefaultBattleNetD2RArgs = "--exec=\"launch OSI\"";

    private readonly VmAgentConfig _config;
    private readonly object _activityLock = new();
    private D2RActivityState _activityState = D2RActivityState.Unknown;
    private DateTimeOffset? _characterScreenIdleSinceUtc;
    private DateTimeOffset? _lastLobbyOrGameInteractionUtc;
    private string? _lastActivityReason;

    public VmOperations(VmAgentConfig config)
    {
        _config = config;
    }

    public Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var battleNetRunning = IsProcessRunning(_config.BattleNetProcessName);
        var d2rRunning = IsProcessRunning(_config.D2RProcessName);
        if (!d2rRunning)
        {
            ClearD2RActivity();
        }

        var activity = GetActivitySnapshot();

        return Task.FromResult<object>(new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            battleNetRunning,
            d2rRunning,
            d2rActivityState = activity.State.ToString(),
            characterScreenIdleSinceUtc = activity.CharacterScreenIdleSinceUtc,
            lastLobbyOrGameInteractionUtc = activity.LastLobbyOrGameInteractionUtc,
            lastActivityReason = activity.Reason,
            idleQuitEnabled = _config.IdleQuitEnabled,
            idleQuitMinutes = _config.IdleQuitMinutes,
            timeUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task<CommandResult> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "status" => CommandResult.Success("Status collected.", await GetStatusAsync(cancellationToken)),
            "launch_battlenet" => LaunchBattleNet(),
            "launch_d2r" => await LaunchD2RAsync(cancellationToken),
            "kill_d2r" => KillD2R(),
            "quit_d2r" => await QuitD2RAsync(cancellationToken),
            "restart_d2r" => await RestartD2RAsync(cancellationToken),
            "screenshot" => await TakeScreenshotAsync(cancellationToken),
            "menu_ready" => await ReadyClientAsync(cancellationToken),
            "menu_lobby" => await GoLobbyAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_play" => await PlayCharacterAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_game" => await JoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_create_game" => await CreateGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_friend" => await JoinFriendAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_save_exit" => await SaveAndExitAsync(cancellationToken),
            _ => CommandResult.Failure($"Unsupported VM command: {request.Command}")
        };
    }

    private async Task<CommandResult> LaunchD2RAsync(CancellationToken cancellationToken)
    {
        if (IsProcessRunning(_config.D2RProcessName))
        {
            MarkCharacterScreenIdleIfNotActive("D2R was already running when launch was requested.");
            return CommandResult.Success("D2R is already running.", await GetStatusAsync(cancellationToken));
        }

        var battleNetWasRunning = IsProcessRunning(_config.BattleNetProcessName);
        await PrepareDesktopForD2RLaunchAsync(battleNetWasRunning, cancellationToken);

        var usedBattleNetExec = false;
        var launchAttempts = 1;

        if (!_config.PreferBattleNetExecLaunch && !string.IsNullOrWhiteSpace(_config.D2RPath))
        {
            var launch = LaunchProcess(_config.D2RPath, _config.D2RArgs);
            if (!launch.Ok)
            {
                return launch;
            }
        }
        else
        {
            usedBattleNetExec = true;
            var launch = LaunchBattleNetD2R();
            if (!launch.Ok)
            {
                return launch;
            }
        }

        if (usedBattleNetExec && !battleNetWasRunning)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.BattleNetExecRetryDelaySeconds, 1)), cancellationToken);
            if (!IsProcessRunning(_config.D2RProcessName))
            {
                var retry = LaunchBattleNetD2R();
                if (!retry.Ok)
                {
                    return retry;
                }

                launchAttempts++;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.LaunchGraceSeconds, 1)), cancellationToken);
        if (IsProcessRunning(_config.D2RProcessName))
        {
            MarkCharacterScreenIdle("D2R launch completed.");
        }

        var status = await GetStatusAsync(cancellationToken);
        var message = launchAttempts > 1
            ? "Battle.net cold-started; D2R launch command sent twice. Check status for final client state."
            : "Launch command sent. Check status for final client state.";
        return CommandResult.Success(message, status);
    }

    private async Task PrepareDesktopForD2RLaunchAsync(bool battleNetWasRunning, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = new WindowsInput();
        input.ShowDesktop();
        await DelayStepAsync(cancellationToken);

        if (!battleNetWasRunning)
        {
            return;
        }

        try
        {
            input.FocusProcess(_config.BattleNetProcessName);
            await DelayStepAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Battle.net may be between windows during startup; launching can still proceed.
        }
    }

    private async Task<CommandResult> RestartD2RAsync(CancellationToken cancellationToken)
    {
        KillD2R();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await LaunchD2RAsync(cancellationToken);
    }

    public async Task RunIdleMonitorAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.IdleQuitCheckSeconds, 10));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            try
            {
                await QuitIfCharacterScreenIdleAsync(log ?? (_ => { }), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                log?.Invoke($"Idle monitor failed: {ex.Message}");
            }
        }
    }

    private async Task QuitIfCharacterScreenIdleAsync(Action<string> log, CancellationToken cancellationToken)
    {
        if (!_config.IdleQuitEnabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (!IsProcessRunning(_config.D2RProcessName))
        {
            ClearD2RActivity();
            return;
        }

        var activity = GetActivitySnapshot();
        if (activity.State != D2RActivityState.CharacterScreenIdle
            || activity.CharacterScreenIdleSinceUtc is not { } idleSince)
        {
            return;
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(_config.IdleQuitMinutes, 1));
        var idleFor = DateTimeOffset.UtcNow - idleSince;
        if (idleFor < timeout)
        {
            return;
        }

        log($"D2R has been idle at the character screen for {idleFor.TotalMinutes:N0} minute(s); sending Alt+F4.");
        var result = await QuitD2RAsync(cancellationToken);
        if (!result.Ok)
        {
            log($"Idle quit failed: {result.Message}");
        }
    }

    private async Task<CommandResult> QuitD2RAsync(CancellationToken cancellationToken)
    {
        if (!IsProcessRunning(_config.D2RProcessName))
        {
            ClearD2RActivity();
            return CommandResult.Success("D2R is not running.", await GetStatusAsync(cancellationToken));
        }

        var input = new WindowsInput();
        if (!input.TryFocusProcess(_config.D2RProcessName))
        {
            return CommandResult.Failure("D2R is running, but no focusable window was found.");
        }

        await DelayStepAsync(cancellationToken);
        input.PressAltF4();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        ClearD2RActivity();
        return CommandResult.Success("Alt+F4 sent to D2R.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> ReadyClientAsync(CancellationToken cancellationToken)
    {
        var launch = await LaunchD2RAsync(cancellationToken);
        if (!launch.Ok)
        {
            return launch;
        }

        var input = new WindowsInput();
        await DelayLongAsync(cancellationToken);

        if (!IsProcessRunning(_config.D2RProcessName)
            && IsProcessRunning(_config.BattleNetProcessName)
            && _config.Ui.ClickBattleNetPlayWhenNeeded)
        {
            var battleNetFocused = await TryFocusProcessUntilAsync(
                input,
                _config.BattleNetProcessName,
                TimeSpan.FromSeconds(Math.Max(_config.Ui.WindowFocusTimeoutSeconds, 1)),
                cancellationToken);
            if (!battleNetFocused)
            {
                return CommandResult.Failure("Battle.net is running, but no focusable window was found yet.", await GetStatusAsync(cancellationToken));
            }

            await DelayStepAsync(cancellationToken);
            input.LeftClick(_config.Ui.BattleNetPlayButton);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.Ui.BattleNetPlayGraceSeconds, 1)), cancellationToken);
        }

        if (!IsProcessRunning(_config.D2RProcessName))
        {
            return CommandResult.Failure("Battle.net is ready, but D2R was not detected yet.", await GetStatusAsync(cancellationToken));
        }

        var d2rFocused = await TryFocusProcessUntilAsync(
            input,
            _config.D2RProcessName,
            TimeSpan.FromSeconds(Math.Max(_config.Ui.WindowFocusTimeoutSeconds, 1)),
            cancellationToken);
        if (!d2rFocused)
        {
            return CommandResult.Failure("D2R is running, but no focusable window was found yet.", await GetStatusAsync(cancellationToken));
        }

        await DelayStepAsync(cancellationToken);
        await ClickThroughIntroAsync(input, cancellationToken);
        await PressThroughTitleScreenAsync(input, cancellationToken);
        await DelayCharacterScreenSettleAsync(cancellationToken);
        input.FocusProcess(_config.D2RProcessName);
        MarkCharacterScreenIdle("Ready flow completed.");
        return CommandResult.Success("D2R ready flow completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> GoLobbyAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        input.LeftClick(_config.Ui.CharacterLobbyButton);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyLoadSeconds, 1)), cancellationToken);
        MarkLobbyOrGameInteraction("Opened Lobby.");
        return CommandResult.Success("Lobby command completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> PlayCharacterAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        input.LeftClick(_config.Ui.CharacterPlayButton);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Clicked Play.");
        return CommandResult.Success("Play character command completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> JoinGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_join_game.");
        }

        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        input.LeftClick(_config.Ui.JoinGameTab);
        await DelayStepAsync(cancellationToken);
        await SelectJoinDifficultyAsync(input, args.Difficulty, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinGameNameField, args.GameName, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinPasswordField, args.Password ?? "", cancellationToken);
        input.LeftClick(_config.Ui.JoinGameButton);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction($"Joined game {args.GameName}.");
        return CommandResult.Success($"Join game flow completed for {args.GameName}.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> CreateGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_create_game.");
        }

        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        input.LeftClick(_config.Ui.CreateGameTab);
        await DelayStepAsync(cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreateGameNameField, args.GameName, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreatePasswordField, args.Password ?? "", cancellationToken);
        input.LeftClick(GetCreateDifficultyPoint(args.Difficulty));
        await DelayStepAsync(cancellationToken);
        input.LeftClick(_config.Ui.CreateGameButton);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction($"Created game {args.GameName}.");
        return CommandResult.Success($"Create game flow completed for {args.GameName}.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> JoinFriendAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        input.LeftClick(_config.Ui.LobbyPartyIcon);
        await DelayLongAsync(cancellationToken);
        input.RightClick(GetFriendRowPoint(args.FriendRow));
        await DelayStepAsync(cancellationToken);
        input.LeftClick(_config.Ui.FriendContextJoinGame);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Joined friend game.");
        return CommandResult.Success("Join friend/follow flow completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> SaveAndExitAsync(CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        input.PressEscape();
        await DelayStepAsync(cancellationToken);
        input.LeftClick(_config.Ui.SaveAndExitButton);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyLoadSeconds, 1)), cancellationToken);
        MarkCharacterScreenIdle("Save and Exit completed.");
        return CommandResult.Success("Save and Exit flow completed.", await GetStatusAsync(cancellationToken));
    }

    private CommandResult KillD2R()
    {
        var result = KillProcess(_config.D2RProcessName);
        ClearD2RActivity();
        return result;
    }

    private void MarkCharacterScreenIdle(string reason)
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.CharacterScreenIdle;
            _characterScreenIdleSinceUtc = DateTimeOffset.UtcNow;
            _lastActivityReason = reason;
        }
    }

    private void MarkCharacterScreenIdleIfNotActive(string reason)
    {
        lock (_activityLock)
        {
            if (_activityState == D2RActivityState.LobbyOrGame)
            {
                return;
            }

            _activityState = D2RActivityState.CharacterScreenIdle;
            _characterScreenIdleSinceUtc ??= DateTimeOffset.UtcNow;
            _lastActivityReason = reason;
        }
    }

    private void MarkLobbyOrGameInteraction(string reason)
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.LobbyOrGame;
            _characterScreenIdleSinceUtc = null;
            _lastLobbyOrGameInteractionUtc = DateTimeOffset.UtcNow;
            _lastActivityReason = reason;
        }
    }

    private void ClearD2RActivity()
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.Unknown;
            _characterScreenIdleSinceUtc = null;
            _lastActivityReason = null;
        }
    }

    private ActivitySnapshot GetActivitySnapshot()
    {
        lock (_activityLock)
        {
            return new ActivitySnapshot(
                _activityState,
                _characterScreenIdleSinceUtc,
                _lastLobbyOrGameInteractionUtc,
                _lastActivityReason);
        }
    }

    private WindowsInput FocusD2R()
    {
        var input = new WindowsInput();
        input.FocusProcess(_config.D2RProcessName);
        return input;
    }

    private async Task ClickThroughIntroAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        for (var index = 0; index < Math.Max(_config.Ui.IntroClickCount, 0); index++)
        {
            input.FocusProcess(_config.D2RProcessName);
            input.LeftClick(_config.Ui.IntroSkipPoint);
            await Task.Delay(Math.Max(_config.Ui.IntroClickDelayMs, 100), cancellationToken);
        }
    }

    private async Task PressThroughTitleScreenAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        for (var index = 0; index < Math.Max(_config.Ui.TitleScreenKeyPressCount, 0); index++)
        {
            input.FocusProcess(_config.D2RProcessName);
            input.PressStartKey();
            await Task.Delay(Math.Max(_config.Ui.TitleScreenKeyPressDelayMs, 100), cancellationToken);
        }
    }

    private async Task DelayCharacterScreenSettleAsync(CancellationToken cancellationToken)
    {
        var settleSeconds = Math.Max(_config.Ui.CharacterScreenSettleSeconds, 0);
        if (settleSeconds == 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(settleSeconds), cancellationToken);
    }

    private async Task SelectCharacterAsync(
        WindowsInput input,
        int? characterSlot,
        CancellationToken cancellationToken)
    {
        input.LeftClick(GetCharacterSlotPoint(characterSlot));
        await DelayStepAsync(cancellationToken);
    }

    private async Task FillTextFieldAsync(
        WindowsInput input,
        AgentCommon.UiPoint point,
        string value,
        CancellationToken cancellationToken)
    {
        input.LeftClick(point);
        await DelayStepAsync(cancellationToken);
        input.LeftClick(point);
        await DelayStepAsync(cancellationToken);
        input.SelectAll();
        await DelayStepAsync(cancellationToken);
        input.TypeText(value);
        await DelayStepAsync(cancellationToken);
    }

    private async Task SelectJoinDifficultyAsync(
        WindowsInput input,
        string? difficulty,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return;
        }

        input.LeftClick(_config.Ui.JoinDifficultyDropdown);
        await DelayStepAsync(cancellationToken);
        input.LeftClick(GetJoinDifficultyPoint(difficulty));
        await DelayStepAsync(cancellationToken);
    }

    private async Task WaitForGameEntryAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        var delaySeconds = _config.Ui.ToggleLegacyGraphicsAfterEnteringGame
            ? Math.Max(_config.Ui.LegacyGraphicsToggleDelaySeconds, 1)
            : Math.Max(_config.Ui.GameLoadSeconds, 1);

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

        if (!_config.Ui.ToggleLegacyGraphicsAfterEnteringGame)
        {
            return;
        }

        input.FocusProcess(_config.D2RProcessName);
        await DelayStepAsync(cancellationToken);
        input.PressLegacyGraphicsToggle();
        await DelayStepAsync(cancellationToken);
    }

    private AgentCommon.UiPoint GetCharacterSlotPoint(int? characterSlot)
    {
        var slot = characterSlot ?? _config.Ui.DefaultCharacterSlot;
        if (slot < 1 || slot > _config.Ui.CharacterSlots.Length)
        {
            throw new InvalidOperationException($"Character slot must be between 1 and {_config.Ui.CharacterSlots.Length}.");
        }

        return _config.Ui.CharacterSlots[slot - 1];
    }

    private AgentCommon.UiPoint GetFriendRowPoint(int? friendRow)
    {
        var row = friendRow ?? _config.Ui.DefaultFriendRow;
        if (row < 1)
        {
            throw new InvalidOperationException("friendRow must be 1 or greater.");
        }

        return new AgentCommon.UiPoint(
            _config.Ui.FriendRowStart.X,
            _config.Ui.FriendRowStart.Y + ((row - 1) * _config.Ui.FriendRowHeight));
    }

    private AgentCommon.UiPoint GetCreateDifficultyPoint(string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => _config.Ui.CreateNightmareButton,
            "hell" => _config.Ui.CreateHellButton,
            _ => _config.Ui.CreateNormalButton
        };
    }

    private AgentCommon.UiPoint GetJoinDifficultyPoint(string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => _config.Ui.JoinDifficultyNightmareOption,
            "hell" => _config.Ui.JoinDifficultyHellOption,
            _ => _config.Ui.JoinDifficultyNormalOption
        };
    }

    private static string NormalizeDifficulty(string? difficulty)
    {
        return string.IsNullOrWhiteSpace(difficulty)
            ? "normal"
            : difficulty.Trim().ToLowerInvariant();
    }

    private Task DelayStepAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Max(_config.Ui.StepDelayMs, 50), cancellationToken);
    }

    private Task DelayLongAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Max(_config.Ui.LongDelayMs, 100), cancellationToken);
    }

    private async Task<bool> TryFocusProcessUntilAsync(
        WindowsInput input,
        string processName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (input.TryFocusProcess(processName))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The process may appear before its main window is ready.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await DelayStepAsync(cancellationToken);
        }
    }

    private CommandResult LaunchBattleNet()
    {
        if (IsProcessRunning(_config.BattleNetProcessName))
        {
            return CommandResult.Success("Battle.net is already running.");
        }

        if (string.IsNullOrWhiteSpace(_config.BattleNetPath))
        {
            return CommandResult.Failure("battleNetPath is not configured.");
        }

        return LaunchProcess(_config.BattleNetPath, _config.BattleNetArgs);
    }

    private CommandResult LaunchBattleNetD2R()
    {
        var path = ResolveBattleNetExecutablePath();
        var args = string.IsNullOrWhiteSpace(_config.BattleNetArgs)
            ? DefaultBattleNetD2RArgs
            : _config.BattleNetArgs;

        return LaunchProcess(path, args);
    }

    private string ResolveBattleNetExecutablePath()
    {
        if (string.IsNullOrWhiteSpace(_config.BattleNetPath))
        {
            return DefaultBattleNetPath;
        }

        var fileName = Path.GetFileName(_config.BattleNetPath);
        if (fileName.Equals("Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(_config.BattleNetPath) is { } directory)
        {
            var battleNetExe = Path.Combine(directory, "Battle.net.exe");
            if (File.Exists(battleNetExe))
            {
                return battleNetExe;
            }
        }

        return _config.BattleNetPath;
    }

    private CommandResult LaunchProcess(string path, string? args)
    {
        var isProtocolLaunch = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
        if (!isProtocolLaunch && !File.Exists(path))
        {
            return CommandResult.Failure($"Launch target was not found: {path}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args ?? "",
            WorkingDirectory = !isProtocolLaunch && !string.IsNullOrWhiteSpace(_config.WorkingDirectory)
                ? _config.WorkingDirectory
                : !isProtocolLaunch
                    ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
                    : Environment.CurrentDirectory,
            UseShellExecute = true
        };

        try
        {
            Process.Start(startInfo);
            return CommandResult.Success($"Started {(isProtocolLaunch ? path : Path.GetFileName(path))}.");
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to start {path}: {ex.Message}");
        }
    }

    private static CommandResult KillProcess(string processName)
    {
        var normalized = NormalizeProcessName(processName);
        var processes = Process.GetProcessesByName(normalized);
        if (processes.Length == 0)
        {
            return CommandResult.Success($"{normalized} was not running.");
        }

        foreach (var process in processes)
        {
            using (process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }

        return CommandResult.Success($"Killed {processes.Length} {normalized} process(es).");
    }

    private static bool IsProcessRunning(string processName)
    {
        return Process.GetProcessesByName(NormalizeProcessName(processName)).Length > 0;
    }

    private static string NormalizeProcessName(string processName)
    {
        return Path.GetFileNameWithoutExtension(processName);
    }

    private async Task<CommandResult> TakeScreenshotAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $stream = New-Object System.IO.MemoryStream
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [Convert]::ToBase64String($stream.ToArray())
            $graphics.Dispose()
            $bitmap.Dispose()
            $stream.Dispose()
            """;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _config.PowerShellPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                error.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.ScreenshotTimeoutSeconds, 5)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return CommandResult.Failure($"Screenshot timed out after {_config.ScreenshotTimeoutSeconds}s.");
        }

        if (process.ExitCode != 0)
        {
            var stderr = error.ToString().Trim();
            return CommandResult.Failure(string.IsNullOrWhiteSpace(stderr) ? "Screenshot failed." : stderr);
        }

        var base64 = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(base64))
        {
            return CommandResult.Failure("Screenshot command did not return image data.");
        }

        return CommandResult.Success(
            "Screenshot captured.",
            new
            {
                mimeType = "image/png",
                base64
            });
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }

    private enum D2RActivityState
    {
        Unknown,
        CharacterScreenIdle,
        LobbyOrGame
    }

    private sealed record ActivitySnapshot(
        D2RActivityState State,
        DateTimeOffset? CharacterScreenIdleSinceUtc,
        DateTimeOffset? LastLobbyOrGameInteractionUtc,
        string? Reason);
}
