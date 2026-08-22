namespace AgentCommon;

public enum D2RUiCoordinateTarget
{
    BattleNetPlayButton,
    BattleNetWhatsNewTitle,
    BattleNetWhatsNewCloseButton,
    BattleNetInstallRequiredContinueButton,
    BattleNetInstallRequiredCancelButton,
    BattleNetLocateGameLink,
    BattleNetFolderPathField,
    BattleNetFolderSelectButton,
    BattleNetInstallConfirmationTitle,
    BattleNetChangeInstallFolder,
    BattleNetStartInstallButton,
    IntroSkipPoint,
    CharacterSlot1,
    CharacterSlot2,
    CharacterSlot3,
    CharacterSlot4,
    CharacterSlot5,
    CharacterSlot6,
    CharacterSlot7,
    CharacterSlot8,
    CharacterPlayButton,
    CharacterLobbyButton,
    CharacterOnlineTab,
    LobbyPartyIcon,
    FriendsAccordionHeader,
    FriendRowStart,
    FriendContextJoinGame,
    JoinGameTab,
    JoinGameNameField,
    JoinPasswordField,
    JoinDifficultyDropdown,
    JoinDifficultyNormalOption,
    JoinDifficultyNightmareOption,
    JoinDifficultyHellOption,
    JoinGameButton,
    GameEntryErrorDialogOkButton,
    CannotJoinCurrentCharacterCancelButton,
    CreateGameTab,
    CreateGameNameField,
    CreatePasswordField,
    CreateNormalButton,
    CreateNightmareButton,
    CreateHellButton,
    CreateGameButton,
    OptionsButton,
    SaveAndExitButton,
    ReturnToGameButton,
    LootFilterButton,
    ChronicleButton,
    HealthGlobe,
    ManaGlobe,
    InGameHudBar
}

public enum D2RUiCoordinateKind
{
    Click,
    Sample
}

public readonly record struct UiPixelPoint(int X, int Y)
{
    public override string ToString()
    {
        return $"{X},{Y}";
    }
}

public sealed record D2RUiCoordinate(
    D2RUiCoordinateTarget Target,
    string Label,
    D2RUiCoordinateKind Kind,
    UiPoint Point,
    UiPixelPoint BaselinePixels,
    string ReferenceAsset,
    string Notes);

public sealed record FriendRowFingerprintRegion(
    UiPoint Center,
    double WidthRatio,
    double HeightRatio,
    int GridColumns,
    int GridRows);

public static class D2RUiCoordinateCatalog
{
    public const int BaselineWidth = 1366;
    public const int BaselineHeight = 768;

    private static readonly D2RUiAutomationConfig Defaults = new();

    public static UiPixelPoint ToBaselinePixels(UiPoint point)
    {
        return ToPixels(point, BaselineWidth, BaselineHeight);
    }

    public static UiPixelPoint ToPixels(UiPoint point, int width, int height)
    {
        return new UiPixelPoint(
            (int)Math.Round(point.X * width),
            (int)Math.Round(point.Y * height));
    }

    public static UiPoint GetPoint(D2RUiAutomationConfig? ui, D2RUiCoordinateTarget target)
    {
        ui ??= Defaults;
        return target switch
        {
            D2RUiCoordinateTarget.BattleNetPlayButton => ChooseBattleNetPlayButton(ui),
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle => Choose(ui.BattleNetWhatsNewTitle, Defaults.BattleNetWhatsNewTitle),
            D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton => Choose(ui.BattleNetWhatsNewCloseButton, Defaults.BattleNetWhatsNewCloseButton),
            D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton => Choose(ui.BattleNetInstallRequiredContinueButton, Defaults.BattleNetInstallRequiredContinueButton),
            D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton => Choose(ui.BattleNetInstallRequiredCancelButton, Defaults.BattleNetInstallRequiredCancelButton),
            D2RUiCoordinateTarget.BattleNetLocateGameLink => Choose(ui.BattleNetLocateGameLink, Defaults.BattleNetLocateGameLink),
            D2RUiCoordinateTarget.BattleNetFolderPathField => Choose(ui.BattleNetFolderPathField, Defaults.BattleNetFolderPathField),
            D2RUiCoordinateTarget.BattleNetFolderSelectButton => Choose(ui.BattleNetFolderSelectButton, Defaults.BattleNetFolderSelectButton),
            D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle => Choose(ui.BattleNetInstallConfirmationTitle, Defaults.BattleNetInstallConfirmationTitle),
            D2RUiCoordinateTarget.BattleNetChangeInstallFolder => Choose(ui.BattleNetChangeInstallFolder, Defaults.BattleNetChangeInstallFolder),
            D2RUiCoordinateTarget.BattleNetStartInstallButton => Choose(ui.BattleNetStartInstallButton, Defaults.BattleNetStartInstallButton),
            D2RUiCoordinateTarget.IntroSkipPoint => Choose(ui.IntroSkipPoint, Defaults.IntroSkipPoint),
            D2RUiCoordinateTarget.CharacterSlot1 => GetCharacterSlotPoint(ui, 1),
            D2RUiCoordinateTarget.CharacterSlot2 => GetCharacterSlotPoint(ui, 2),
            D2RUiCoordinateTarget.CharacterSlot3 => GetCharacterSlotPoint(ui, 3),
            D2RUiCoordinateTarget.CharacterSlot4 => GetCharacterSlotPoint(ui, 4),
            D2RUiCoordinateTarget.CharacterSlot5 => GetCharacterSlotPoint(ui, 5),
            D2RUiCoordinateTarget.CharacterSlot6 => GetCharacterSlotPoint(ui, 6),
            D2RUiCoordinateTarget.CharacterSlot7 => GetCharacterSlotPoint(ui, 7),
            D2RUiCoordinateTarget.CharacterSlot8 => GetCharacterSlotPoint(ui, 8),
            D2RUiCoordinateTarget.CharacterPlayButton => Choose(ui.CharacterPlayButton, Defaults.CharacterPlayButton),
            D2RUiCoordinateTarget.CharacterLobbyButton => Choose(ui.CharacterLobbyButton, Defaults.CharacterLobbyButton),
            D2RUiCoordinateTarget.CharacterOnlineTab => Choose(ui.CharacterOnlineTab, Defaults.CharacterOnlineTab),
            D2RUiCoordinateTarget.LobbyPartyIcon => Choose(ui.LobbyPartyIcon, Defaults.LobbyPartyIcon),
            D2RUiCoordinateTarget.FriendsAccordionHeader => Choose(ui.FriendsAccordionHeader, Defaults.FriendsAccordionHeader),
            D2RUiCoordinateTarget.FriendRowStart => Choose(ui.FriendRowStart, Defaults.FriendRowStart),
            D2RUiCoordinateTarget.FriendContextJoinGame => ChooseFriendContextJoinGame(ui),
            D2RUiCoordinateTarget.JoinGameTab => Choose(ui.JoinGameTab, Defaults.JoinGameTab),
            D2RUiCoordinateTarget.JoinGameNameField => Choose(ui.JoinGameNameField, Defaults.JoinGameNameField),
            D2RUiCoordinateTarget.JoinPasswordField => Choose(ui.JoinPasswordField, Defaults.JoinPasswordField),
            D2RUiCoordinateTarget.JoinDifficultyDropdown => Choose(ui.JoinDifficultyDropdown, Defaults.JoinDifficultyDropdown),
            D2RUiCoordinateTarget.JoinDifficultyNormalOption => Choose(ui.JoinDifficultyNormalOption, Defaults.JoinDifficultyNormalOption),
            D2RUiCoordinateTarget.JoinDifficultyNightmareOption => Choose(ui.JoinDifficultyNightmareOption, Defaults.JoinDifficultyNightmareOption),
            D2RUiCoordinateTarget.JoinDifficultyHellOption => Choose(ui.JoinDifficultyHellOption, Defaults.JoinDifficultyHellOption),
            D2RUiCoordinateTarget.JoinGameButton => Choose(ui.JoinGameButton, Defaults.JoinGameButton),
            D2RUiCoordinateTarget.GameEntryErrorDialogOkButton => Choose(ui.GameEntryErrorDialogOkButton, Defaults.GameEntryErrorDialogOkButton),
            D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton => Choose(ui.CannotJoinCurrentCharacterCancelButton, Defaults.CannotJoinCurrentCharacterCancelButton),
            D2RUiCoordinateTarget.CreateGameTab => Choose(ui.CreateGameTab, Defaults.CreateGameTab),
            D2RUiCoordinateTarget.CreateGameNameField => Choose(ui.CreateGameNameField, Defaults.CreateGameNameField),
            D2RUiCoordinateTarget.CreatePasswordField => Choose(ui.CreatePasswordField, Defaults.CreatePasswordField),
            D2RUiCoordinateTarget.CreateNormalButton => Choose(ui.CreateNormalButton, Defaults.CreateNormalButton),
            D2RUiCoordinateTarget.CreateNightmareButton => Choose(ui.CreateNightmareButton, Defaults.CreateNightmareButton),
            D2RUiCoordinateTarget.CreateHellButton => Choose(ui.CreateHellButton, Defaults.CreateHellButton),
            D2RUiCoordinateTarget.CreateGameButton => Choose(ui.CreateGameButton, Defaults.CreateGameButton),
            D2RUiCoordinateTarget.OptionsButton => Choose(ui.OptionsButton, Defaults.OptionsButton),
            D2RUiCoordinateTarget.SaveAndExitButton => Choose(ui.SaveAndExitButton, Defaults.SaveAndExitButton),
            D2RUiCoordinateTarget.ReturnToGameButton => Choose(ui.ReturnToGameButton, Defaults.ReturnToGameButton),
            D2RUiCoordinateTarget.LootFilterButton => Choose(ui.LootFilterButton, Defaults.LootFilterButton),
            D2RUiCoordinateTarget.ChronicleButton => Choose(ui.ChronicleButton, Defaults.ChronicleButton),
            D2RUiCoordinateTarget.HealthGlobe => Choose(ui.HealthGlobe, Defaults.HealthGlobe),
            D2RUiCoordinateTarget.ManaGlobe => Choose(ui.ManaGlobe, Defaults.ManaGlobe),
            D2RUiCoordinateTarget.InGameHudBar => Choose(ui.InGameHudBar, Defaults.InGameHudBar),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown D2R UI coordinate target.")
        };
    }

    public static UiPoint GetCharacterSlotPoint(D2RUiAutomationConfig? ui, int? characterSlot)
    {
        ui ??= Defaults;
        var slot = characterSlot ?? ui.DefaultCharacterSlot;
        if (characterSlot.HasValue && (slot < 1 || slot > Defaults.CharacterSlots.Length))
        {
            throw new InvalidOperationException($"Character slot must be between 1 and {Defaults.CharacterSlots.Length}.");
        }

        if (slot < 1 || slot > Defaults.CharacterSlots.Length)
        {
            slot = Defaults.DefaultCharacterSlot;
        }

        var fallback = Defaults.CharacterSlots[slot - 1];
        if (ui.CharacterSlots is null || slot > ui.CharacterSlots.Length)
        {
            return Copy(fallback);
        }

        return Choose(ui.CharacterSlots[slot - 1], fallback);
    }

    public static UiPoint GetFriendRowPoint(D2RUiAutomationConfig? ui, int? friendRow)
    {
        ui ??= Defaults;
        var row = friendRow ?? ui.DefaultFriendRow;
        if (friendRow.HasValue && row < 1)
        {
            throw new InvalidOperationException("friendRow must be 1 or greater.");
        }

        if (row < 1)
        {
            row = Defaults.DefaultFriendRow;
        }

        var start = Choose(ui.FriendRowStart, Defaults.FriendRowStart);
        var rowHeight = IsFiniteNormalizedRowHeight(ui.FriendRowHeight)
            ? ui.FriendRowHeight
            : Defaults.FriendRowHeight;
        return new UiPoint(start.X, Clamp01(start.Y + ((row - 1) * rowHeight)));
    }

    public static UiPoint GetFriendContextJoinGamePoint(D2RUiAutomationConfig? ui, int? friendRow)
    {
        ui ??= Defaults;
        var rowPoint = GetFriendRowPoint(ui, friendRow);
        var row1Point = GetFriendRowPoint(ui, 1);
        var row1JoinGame = GetPoint(ui, D2RUiCoordinateTarget.FriendContextJoinGame);

        return new UiPoint(
            Clamp01(rowPoint.X + (row1JoinGame.X - row1Point.X)),
            Clamp01(rowPoint.Y + (row1JoinGame.Y - row1Point.Y)));
    }

    // The capture region for a follow-bind fingerprint: the same row math GetFriendRowPoint uses,
    // shifted to the name-text sub-area of that row instead of the row's right-click center.
    public static FriendRowFingerprintRegion GetFriendRowFingerprintRegion(D2RUiAutomationConfig? ui, int row)
    {
        ui ??= Defaults;
        var rowPoint = GetFriendRowPoint(ui, row);
        var offsetX = IsFiniteRatio(ui.FriendRowFingerprintOffsetX) ? ui.FriendRowFingerprintOffsetX : Defaults.FriendRowFingerprintOffsetX;
        var offsetY = ResolveLegacyRatio(
            ui.FriendRowFingerprintOffsetY, LegacyFingerprintOffsetY, Defaults.FriendRowFingerprintOffsetY, requirePositive: false);
        var widthRatio = IsFinitePositiveRatio(ui.FriendRowFingerprintWidthRatio) ? ui.FriendRowFingerprintWidthRatio : Defaults.FriendRowFingerprintWidthRatio;
        var heightRatio = ResolveLegacyRatio(
            ui.FriendRowFingerprintHeightRatio, LegacyFingerprintHeightRatio, Defaults.FriendRowFingerprintHeightRatio, requirePositive: true);
        var columns = ResolveFriendRowFingerprintGridColumns(ui.FriendRowFingerprintGridColumns);
        var gridRows = ResolveFriendRowFingerprintGridRows(ui.FriendRowFingerprintGridRows);

        var center = new UiPoint(Clamp01(rowPoint.X + offsetX), Clamp01(rowPoint.Y + offsetY));
        return new FriendRowFingerprintRegion(center, widthRatio, heightRatio, columns, gridRows);
    }

    // Every agent config written by the first-run wizard materializes the whole ui object, so a
    // fleet that was set up before this change carries an explicit 24 in its JSON and would ignore
    // the raised default forever - the operator would have to hand-edit the file on every VM. 24
    // was never a considered choice, only the value that happened to ship, so it is treated as
    // "not customized" and migrated. Any other value, including a deliberate 23 or 25, is honored
    // exactly as written.
    //
    // Both the capture path and the scan path read their geometry through this one function, so
    // they cannot end up disagreeing about the grid - which would silently produce templates that
    // compare against nothing.
    // Every value the friend-row fingerprint band has ever shipped with. All four fields move
    // together whenever the band is re-measured, and a fleet configured under any earlier release
    // carries the old numbers explicitly, so each one needs the same treatment.
    private const int LegacyFriendRowFingerprintGridColumns = 24;
    private const int LegacyFriendRowFingerprintGridColumnsV2 = 32;
    private const int LegacyFriendRowFingerprintGridRows = 4;
    private const double LegacyFingerprintOffsetY = -0.010;
    private const double LegacyFingerprintHeightRatio = 0.022;
    private const double LegacyRatioTolerance = 1e-9;

    internal static int ResolveFriendRowFingerprintGridColumns(int configuredColumns)
    {
        return configuredColumns is <= 0
            or LegacyFriendRowFingerprintGridColumns
            or LegacyFriendRowFingerprintGridColumnsV2
            ? Defaults.FriendRowFingerprintGridColumns
            : configuredColumns;
    }

    internal static int ResolveFriendRowFingerprintGridRows(int configuredRows)
    {
        return configuredRows is <= 0 or LegacyFriendRowFingerprintGridRows
            ? Defaults.FriendRowFingerprintGridRows
            : configuredRows;
    }

    private static double ResolveLegacyRatio(
        double configured,
        double legacyDefault,
        double currentDefault,
        bool requirePositive)
    {
        var usable = requirePositive ? IsFinitePositiveRatio(configured) : IsFiniteRatio(configured);
        return !usable || Math.Abs(configured - legacyDefault) < LegacyRatioTolerance
            ? currentDefault
            : configured;
    }

    private static bool IsFiniteRatio(double value) => double.IsFinite(value);

    private static bool IsFinitePositiveRatio(double value) => double.IsFinite(value) && value > 0;

    public static UiPoint GetCreateDifficultyPoint(D2RUiAutomationConfig? ui, string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => GetPoint(ui, D2RUiCoordinateTarget.CreateNightmareButton),
            "hell" => GetPoint(ui, D2RUiCoordinateTarget.CreateHellButton),
            _ => GetPoint(ui, D2RUiCoordinateTarget.CreateNormalButton)
        };
    }

    public static UiPoint GetJoinDifficultyPoint(D2RUiAutomationConfig? ui, string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => GetPoint(ui, D2RUiCoordinateTarget.JoinDifficultyNightmareOption),
            "hell" => GetPoint(ui, D2RUiCoordinateTarget.JoinDifficultyHellOption),
            _ => GetPoint(ui, D2RUiCoordinateTarget.JoinDifficultyNormalOption)
        };
    }

    public static IReadOnlyList<D2RUiCoordinate> GetAll(D2RUiAutomationConfig? ui = null)
    {
        return Enum.GetValues<D2RUiCoordinateTarget>()
            .Select(target =>
            {
                var point = GetPoint(ui, target);
                return new D2RUiCoordinate(
                    target,
                    GetLabel(target),
                    GetKind(target),
                    point,
                    ToBaselinePixels(point),
                    GetReferenceAsset(target),
                    GetNotes(target));
            })
            .ToArray();
    }

    public static string GetLabel(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.BattleNetPlayButton => "Battle.net Play button",
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle => "Battle.net What's New title sample",
            D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton => "Battle.net What's New close button",
            D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton => "Battle.net Installation Required Continue button sample",
            D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton => "Battle.net Installation Required Cancel button",
            D2RUiCoordinateTarget.BattleNetLocateGameLink => "Battle.net Locate the game link",
            D2RUiCoordinateTarget.BattleNetFolderPathField => "Choose a Folder path field",
            D2RUiCoordinateTarget.BattleNetFolderSelectButton => "Choose a Folder Select Folder button",
            D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle => "Battle.net install-location confirmation title sample",
            D2RUiCoordinateTarget.BattleNetChangeInstallFolder => "Battle.net Change Folder link",
            D2RUiCoordinateTarget.BattleNetStartInstallButton => "Battle.net Start Install/scan button",
            D2RUiCoordinateTarget.IntroSkipPoint => "D2R intro/title continue point",
            D2RUiCoordinateTarget.CharacterSlot1 => "Character slot 1",
            D2RUiCoordinateTarget.CharacterSlot2 => "Character slot 2",
            D2RUiCoordinateTarget.CharacterSlot3 => "Character slot 3",
            D2RUiCoordinateTarget.CharacterSlot4 => "Character slot 4",
            D2RUiCoordinateTarget.CharacterSlot5 => "Character slot 5",
            D2RUiCoordinateTarget.CharacterSlot6 => "Character slot 6",
            D2RUiCoordinateTarget.CharacterSlot7 => "Character slot 7",
            D2RUiCoordinateTarget.CharacterSlot8 => "Character slot 8",
            D2RUiCoordinateTarget.CharacterPlayButton => "Character Play button",
            D2RUiCoordinateTarget.CharacterLobbyButton => "Character Lobby button",
            D2RUiCoordinateTarget.CharacterOnlineTab => "Character Online tab",
            D2RUiCoordinateTarget.LobbyPartyIcon => "Lobby party/friends icon",
            D2RUiCoordinateTarget.FriendsAccordionHeader => "Friends accordion header",
            D2RUiCoordinateTarget.FriendRowStart => "Friends drawer row 1",
            D2RUiCoordinateTarget.FriendContextJoinGame => "Friend context Join Game option",
            D2RUiCoordinateTarget.JoinGameTab => "Lobby Join Game tab",
            D2RUiCoordinateTarget.JoinGameNameField => "Join Game name field",
            D2RUiCoordinateTarget.JoinPasswordField => "Join Game password field",
            D2RUiCoordinateTarget.JoinDifficultyDropdown => "Join Game difficulty dropdown",
            D2RUiCoordinateTarget.JoinDifficultyNormalOption => "Join Game Normal option",
            D2RUiCoordinateTarget.JoinDifficultyNightmareOption => "Join Game Nightmare option",
            D2RUiCoordinateTarget.JoinDifficultyHellOption => "Join Game Hell option",
            D2RUiCoordinateTarget.JoinGameButton => "Final Join Game button",
            D2RUiCoordinateTarget.GameEntryErrorDialogOkButton => "Game-entry error OK button",
            D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton => "Cannot-join-current-character Cancel button",
            D2RUiCoordinateTarget.CreateGameTab => "Lobby Create Game tab",
            D2RUiCoordinateTarget.CreateGameNameField => "Create Game name field",
            D2RUiCoordinateTarget.CreatePasswordField => "Create Game password field",
            D2RUiCoordinateTarget.CreateNormalButton => "Create Normal difficulty button",
            D2RUiCoordinateTarget.CreateNightmareButton => "Create Nightmare difficulty button",
            D2RUiCoordinateTarget.CreateHellButton => "Create Hell difficulty button",
            D2RUiCoordinateTarget.CreateGameButton => "Final Create Game button",
            D2RUiCoordinateTarget.OptionsButton => "Pause menu Options button",
            D2RUiCoordinateTarget.SaveAndExitButton => "Pause menu Save and Exit button",
            D2RUiCoordinateTarget.ReturnToGameButton => "Pause menu Return to Game button",
            D2RUiCoordinateTarget.LootFilterButton => "Pause menu Loot Filter button (Reign of the Warlock)",
            D2RUiCoordinateTarget.ChronicleButton => "Pause menu Chronicle button (Reign of the Warlock)",
            D2RUiCoordinateTarget.HealthGlobe => "Health globe sample",
            D2RUiCoordinateTarget.ManaGlobe => "Mana globe sample",
            D2RUiCoordinateTarget.InGameHudBar => "In-game bottom HUD sample",
            _ => target.ToString()
        };
    }

    public static D2RUiCoordinateKind GetKind(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle
                or D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton
                or D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle
                or D2RUiCoordinateTarget.OptionsButton
                or D2RUiCoordinateTarget.ReturnToGameButton
                or D2RUiCoordinateTarget.LootFilterButton
                or D2RUiCoordinateTarget.ChronicleButton
                or D2RUiCoordinateTarget.HealthGlobe
                or D2RUiCoordinateTarget.ManaGlobe
                or D2RUiCoordinateTarget.InGameHudBar => D2RUiCoordinateKind.Sample,
            _ => D2RUiCoordinateKind.Click
        };
    }

    public static string GetReferenceAsset(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.BattleNetPlayButton
                or D2RUiCoordinateTarget.BattleNetWhatsNewTitle
                or D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton => "logged_in_battle_net.jpg",
            D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton
                or D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton => "1366x768/battlenet_installation_required.png",
            D2RUiCoordinateTarget.BattleNetLocateGameLink => "1366x768/battlenet_d2r_install_landing.png",
            D2RUiCoordinateTarget.BattleNetFolderPathField
                or D2RUiCoordinateTarget.BattleNetFolderSelectButton => "1366x768/battlenet_choose_install_folder.png",
            D2RUiCoordinateTarget.BattleNetStartInstallButton => "1366x768/battlenet_start_install_scan.png",
            D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle
                or D2RUiCoordinateTarget.BattleNetChangeInstallFolder => "1366x768/battlenet_start_install_scan.png",
            D2RUiCoordinateTarget.IntroSkipPoint => "1366x768/post_intro_splash_screen.png",
            D2RUiCoordinateTarget.CharacterSlot1
                or D2RUiCoordinateTarget.CharacterSlot2
                or D2RUiCoordinateTarget.CharacterSlot3
                or D2RUiCoordinateTarget.CharacterSlot4
                or D2RUiCoordinateTarget.CharacterSlot5
                or D2RUiCoordinateTarget.CharacterSlot6
                or D2RUiCoordinateTarget.CharacterSlot7
                or D2RUiCoordinateTarget.CharacterSlot8
                or D2RUiCoordinateTarget.CharacterPlayButton
                or D2RUiCoordinateTarget.CharacterLobbyButton
                or D2RUiCoordinateTarget.CharacterOnlineTab => "1366x768/char_screen_act1.png",
            D2RUiCoordinateTarget.LobbyPartyIcon
                or D2RUiCoordinateTarget.FriendsAccordionHeader
                or D2RUiCoordinateTarget.FriendRowStart
                or D2RUiCoordinateTarget.FriendContextJoinGame => "1366x768/lobby_right_click_friend_join_game_available.png",
            D2RUiCoordinateTarget.JoinGameTab
                or D2RUiCoordinateTarget.JoinGameNameField
                or D2RUiCoordinateTarget.JoinPasswordField
                or D2RUiCoordinateTarget.JoinDifficultyDropdown
                or D2RUiCoordinateTarget.JoinDifficultyNormalOption
                or D2RUiCoordinateTarget.JoinDifficultyNightmareOption
                or D2RUiCoordinateTarget.JoinDifficultyHellOption
                or D2RUiCoordinateTarget.JoinGameButton => "1366x768/lobby_join_game_screen.png",
            D2RUiCoordinateTarget.GameEntryErrorDialogOkButton => "game_and_password_dont_match.jpg",
            D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton => "1366x768/cannot_join_game_with_current_character.png",
            D2RUiCoordinateTarget.CreateGameTab
                or D2RUiCoordinateTarget.CreateGameNameField
                or D2RUiCoordinateTarget.CreatePasswordField
                or D2RUiCoordinateTarget.CreateNormalButton
                or D2RUiCoordinateTarget.CreateNightmareButton
                or D2RUiCoordinateTarget.CreateHellButton
                or D2RUiCoordinateTarget.CreateGameButton => "1366x768/lobby_create_game_screen.png",
            D2RUiCoordinateTarget.OptionsButton
                or D2RUiCoordinateTarget.SaveAndExitButton
                or D2RUiCoordinateTarget.ReturnToGameButton
                or D2RUiCoordinateTarget.LootFilterButton
                or D2RUiCoordinateTarget.ChronicleButton => "1366x768/rotw_ingame_save_and_exit_menu.png",
            D2RUiCoordinateTarget.HealthGlobe
                or D2RUiCoordinateTarget.ManaGlobe
                or D2RUiCoordinateTarget.InGameHudBar => "1366x768/just_landed_in_game_checkforhealthandmanaglobes.png",
            _ => ""
        };
    }

    public static string GetNotes(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.FriendsAccordionHeader => "Click after opening the drawer if the Friends accordion is collapsed.",
            D2RUiCoordinateTarget.FriendRowStart => "Additional rows use friendRowHeight, default 0.049 of window height.",
            D2RUiCoordinateTarget.FriendContextJoinGame => "Row-1 context-menu option; runtime clicks keep the same in-menu offset from the right-clicked friend row because the menu is anchored to the pointer position.",
            D2RUiCoordinateTarget.IntroSkipPoint => "Center click/key target used during intro, splash, and title skip bursts.",
            D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton => "Dismisses the two-button current-character join restriction without switching characters.",
            D2RUiCoordinateTarget.HealthGlobe
                or D2RUiCoordinateTarget.ManaGlobe
                or D2RUiCoordinateTarget.InGameHudBar => "Detection sample, not a click target.",
            D2RUiCoordinateTarget.OptionsButton
                or D2RUiCoordinateTarget.ReturnToGameButton => "Pause-menu detection sample, not a click target.",
            D2RUiCoordinateTarget.LootFilterButton
                or D2RUiCoordinateTarget.ChronicleButton => "Reign of the Warlock pause-menu rows, below the divider. Detection samples only - nothing ever clicks them.",
            D2RUiCoordinateTarget.SaveAndExitButton => "The only pause-menu row the agent ever clicks.",
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle => "Popup detection sample, not a click target.",
            D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton => "Detection sample only. Repair cancels this prompt because Continue starts a new install.",
            D2RUiCoordinateTarget.BattleNetFolderPathField
                or D2RUiCoordinateTarget.BattleNetFolderSelectButton => "Coordinates are relative to the exact-title Choose a Folder dialog, not the Battle.net main window.",
            _ => ""
        };
    }

    private static UiPoint ChooseBattleNetPlayButton(D2RUiAutomationConfig ui)
    {
        var point = Choose(ui.BattleNetPlayButton, Defaults.BattleNetPlayButton);
        // The first shipped value was measured against the whole 1366x768 desktop, while the
        // runtime has always resolved Battle.net points against its client rectangle. Treat that
        // exact persisted default as legacy so existing VM configs move to the measured client-
        // relative primary-action center without hand editing.
        return IsNear(point, x: 0.129, y: 0.703)
            ? Copy(Defaults.BattleNetPlayButton)
            : point;
    }

    private static UiPoint Choose(UiPoint? candidate, UiPoint fallback)
    {
        return IsValid(candidate) ? Copy(candidate!) : Copy(fallback);
    }

    private static UiPoint ChooseFriendContextJoinGame(D2RUiAutomationConfig ui)
    {
        var point = Choose(ui.FriendContextJoinGame, Defaults.FriendContextJoinGame);
        return IsKnownStaleFriendContextJoinGame(point)
            ? Copy(Defaults.FriendContextJoinGame)
            : point;
    }

    private static bool IsKnownStaleFriendContextJoinGame(UiPoint point)
    {
        return IsNear(point, x: 0.318, y: 0.322)
            || IsNear(point, x: 0.318, y: 0.223)
            || IsStaleFriendContextJoinGameFamily(point);
    }

    private static bool IsStaleFriendContextJoinGameFamily(UiPoint point)
    {
        const double xTolerance = 0.004;
        return Math.Abs(point.X - 0.318) <= xTolerance
            && point.Y >= 0.200
            && point.Y <= 0.380;
    }

    private static bool IsNear(UiPoint point, double x, double y)
    {
        const double tolerance = 0.001;
        return Math.Abs(point.X - x) <= tolerance
            && Math.Abs(point.Y - y) <= tolerance;
    }

    private static bool IsValid(UiPoint? point)
    {
        return point is not null
            && double.IsFinite(point.X)
            && double.IsFinite(point.Y)
            && point.X >= 0
            && point.X <= 1
            && point.Y >= 0
            && point.Y <= 1;
    }

    private static UiPoint Copy(UiPoint point)
    {
        return new UiPoint(point.X, point.Y);
    }

    private static bool IsFiniteNormalizedRowHeight(double rowHeight)
    {
        return double.IsFinite(rowHeight)
            && rowHeight > 0
            && rowHeight < 1;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 1);
    }

    private static string NormalizeDifficulty(string? difficulty)
    {
        return string.IsNullOrWhiteSpace(difficulty)
            ? "normal"
            : difficulty.Trim().ToLowerInvariant();
    }
}
