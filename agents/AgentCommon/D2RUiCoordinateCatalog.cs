namespace AgentCommon;

public enum D2RUiCoordinateTarget
{
    BattleNetPlayButton,
    BattleNetWhatsNewTitle,
    BattleNetWhatsNewCloseButton,
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
    CreateGameTab,
    CreateGameNameField,
    CreatePasswordField,
    CreateNormalButton,
    CreateNightmareButton,
    CreateHellButton,
    CreateGameButton,
    SaveAndExitButton,
    ModernHealthGlobe,
    ModernManaGlobe,
    LegacyHealthGlobe,
    LegacyManaGlobe,
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
            D2RUiCoordinateTarget.BattleNetPlayButton => Choose(ui.BattleNetPlayButton, Defaults.BattleNetPlayButton),
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle => Choose(ui.BattleNetWhatsNewTitle, Defaults.BattleNetWhatsNewTitle),
            D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton => Choose(ui.BattleNetWhatsNewCloseButton, Defaults.BattleNetWhatsNewCloseButton),
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
            D2RUiCoordinateTarget.FriendRowStart => Choose(ui.FriendRowStart, Defaults.FriendRowStart),
            D2RUiCoordinateTarget.FriendContextJoinGame => Choose(ui.FriendContextJoinGame, Defaults.FriendContextJoinGame),
            D2RUiCoordinateTarget.JoinGameTab => Choose(ui.JoinGameTab, Defaults.JoinGameTab),
            D2RUiCoordinateTarget.JoinGameNameField => Choose(ui.JoinGameNameField, Defaults.JoinGameNameField),
            D2RUiCoordinateTarget.JoinPasswordField => Choose(ui.JoinPasswordField, Defaults.JoinPasswordField),
            D2RUiCoordinateTarget.JoinDifficultyDropdown => Choose(ui.JoinDifficultyDropdown, Defaults.JoinDifficultyDropdown),
            D2RUiCoordinateTarget.JoinDifficultyNormalOption => Choose(ui.JoinDifficultyNormalOption, Defaults.JoinDifficultyNormalOption),
            D2RUiCoordinateTarget.JoinDifficultyNightmareOption => Choose(ui.JoinDifficultyNightmareOption, Defaults.JoinDifficultyNightmareOption),
            D2RUiCoordinateTarget.JoinDifficultyHellOption => Choose(ui.JoinDifficultyHellOption, Defaults.JoinDifficultyHellOption),
            D2RUiCoordinateTarget.JoinGameButton => Choose(ui.JoinGameButton, Defaults.JoinGameButton),
            D2RUiCoordinateTarget.GameEntryErrorDialogOkButton => Choose(ui.GameEntryErrorDialogOkButton, Defaults.GameEntryErrorDialogOkButton),
            D2RUiCoordinateTarget.CreateGameTab => Choose(ui.CreateGameTab, Defaults.CreateGameTab),
            D2RUiCoordinateTarget.CreateGameNameField => Choose(ui.CreateGameNameField, Defaults.CreateGameNameField),
            D2RUiCoordinateTarget.CreatePasswordField => Choose(ui.CreatePasswordField, Defaults.CreatePasswordField),
            D2RUiCoordinateTarget.CreateNormalButton => Choose(ui.CreateNormalButton, Defaults.CreateNormalButton),
            D2RUiCoordinateTarget.CreateNightmareButton => Choose(ui.CreateNightmareButton, Defaults.CreateNightmareButton),
            D2RUiCoordinateTarget.CreateHellButton => Choose(ui.CreateHellButton, Defaults.CreateHellButton),
            D2RUiCoordinateTarget.CreateGameButton => Choose(ui.CreateGameButton, Defaults.CreateGameButton),
            D2RUiCoordinateTarget.SaveAndExitButton => Choose(ui.SaveAndExitButton, Defaults.SaveAndExitButton),
            D2RUiCoordinateTarget.ModernHealthGlobe => Choose(ui.ModernHealthGlobe, Defaults.ModernHealthGlobe),
            D2RUiCoordinateTarget.ModernManaGlobe => Choose(ui.ModernManaGlobe, Defaults.ModernManaGlobe),
            D2RUiCoordinateTarget.LegacyHealthGlobe => Choose(ui.LegacyHealthGlobe, Defaults.LegacyHealthGlobe),
            D2RUiCoordinateTarget.LegacyManaGlobe => Choose(ui.LegacyManaGlobe, Defaults.LegacyManaGlobe),
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

    // The capture region for a follow-bind fingerprint: the same row math GetFriendRowPoint uses,
    // shifted to the name-text sub-area of that row instead of the row's right-click center.
    public static FriendRowFingerprintRegion GetFriendRowFingerprintRegion(D2RUiAutomationConfig? ui, int row)
    {
        ui ??= Defaults;
        var rowPoint = GetFriendRowPoint(ui, row);
        var offsetX = IsFiniteRatio(ui.FriendRowFingerprintOffsetX) ? ui.FriendRowFingerprintOffsetX : Defaults.FriendRowFingerprintOffsetX;
        var offsetY = IsFiniteRatio(ui.FriendRowFingerprintOffsetY) ? ui.FriendRowFingerprintOffsetY : Defaults.FriendRowFingerprintOffsetY;
        var widthRatio = IsFinitePositiveRatio(ui.FriendRowFingerprintWidthRatio) ? ui.FriendRowFingerprintWidthRatio : Defaults.FriendRowFingerprintWidthRatio;
        var heightRatio = IsFinitePositiveRatio(ui.FriendRowFingerprintHeightRatio) ? ui.FriendRowFingerprintHeightRatio : Defaults.FriendRowFingerprintHeightRatio;
        var columns = ui.FriendRowFingerprintGridColumns > 0 ? ui.FriendRowFingerprintGridColumns : Defaults.FriendRowFingerprintGridColumns;
        var gridRows = ui.FriendRowFingerprintGridRows > 0 ? ui.FriendRowFingerprintGridRows : Defaults.FriendRowFingerprintGridRows;

        var center = new UiPoint(Clamp01(rowPoint.X + offsetX), Clamp01(rowPoint.Y + offsetY));
        return new FriendRowFingerprintRegion(center, widthRatio, heightRatio, columns, gridRows);
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
            D2RUiCoordinateTarget.CreateGameTab => "Lobby Create Game tab",
            D2RUiCoordinateTarget.CreateGameNameField => "Create Game name field",
            D2RUiCoordinateTarget.CreatePasswordField => "Create Game password field",
            D2RUiCoordinateTarget.CreateNormalButton => "Create Normal difficulty button",
            D2RUiCoordinateTarget.CreateNightmareButton => "Create Nightmare difficulty button",
            D2RUiCoordinateTarget.CreateHellButton => "Create Hell difficulty button",
            D2RUiCoordinateTarget.CreateGameButton => "Final Create Game button",
            D2RUiCoordinateTarget.SaveAndExitButton => "Save and Exit button",
            D2RUiCoordinateTarget.ModernHealthGlobe => "Modern health globe sample",
            D2RUiCoordinateTarget.ModernManaGlobe => "Modern mana globe sample",
            D2RUiCoordinateTarget.LegacyHealthGlobe => "Legacy health globe sample",
            D2RUiCoordinateTarget.LegacyManaGlobe => "Legacy mana globe sample",
            D2RUiCoordinateTarget.InGameHudBar => "In-game bottom HUD sample",
            _ => target.ToString()
        };
    }

    public static D2RUiCoordinateKind GetKind(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle
                or D2RUiCoordinateTarget.ModernHealthGlobe
                or D2RUiCoordinateTarget.ModernManaGlobe
                or D2RUiCoordinateTarget.LegacyHealthGlobe
                or D2RUiCoordinateTarget.LegacyManaGlobe
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
            D2RUiCoordinateTarget.CreateGameTab
                or D2RUiCoordinateTarget.CreateGameNameField
                or D2RUiCoordinateTarget.CreatePasswordField
                or D2RUiCoordinateTarget.CreateNormalButton
                or D2RUiCoordinateTarget.CreateNightmareButton
                or D2RUiCoordinateTarget.CreateHellButton
                or D2RUiCoordinateTarget.CreateGameButton => "1366x768/lobby_create_game_screen.png",
            D2RUiCoordinateTarget.SaveAndExitButton => "in-game escape menu",
            D2RUiCoordinateTarget.ModernHealthGlobe
                or D2RUiCoordinateTarget.ModernManaGlobe
                or D2RUiCoordinateTarget.LegacyHealthGlobe
                or D2RUiCoordinateTarget.LegacyManaGlobe
                or D2RUiCoordinateTarget.InGameHudBar => "1366x768/just_landed_in_game_checkforhealthandmanaglobes.png",
            _ => ""
        };
    }

    public static string GetNotes(D2RUiCoordinateTarget target)
    {
        return target switch
        {
            D2RUiCoordinateTarget.FriendRowStart => "Additional rows use friendRowHeight, default 0.049 of window height.",
            D2RUiCoordinateTarget.IntroSkipPoint => "Center click/key target used during intro, splash, and title skip bursts.",
            D2RUiCoordinateTarget.ModernHealthGlobe
                or D2RUiCoordinateTarget.ModernManaGlobe
                or D2RUiCoordinateTarget.LegacyHealthGlobe
                or D2RUiCoordinateTarget.LegacyManaGlobe
                or D2RUiCoordinateTarget.InGameHudBar => "Detection sample, not a click target.",
            D2RUiCoordinateTarget.BattleNetWhatsNewTitle => "Popup detection sample, not a click target.",
            _ => ""
        };
    }

    private static UiPoint Choose(UiPoint? candidate, UiPoint fallback)
    {
        return IsValid(candidate) ? Copy(candidate!) : Copy(fallback);
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
