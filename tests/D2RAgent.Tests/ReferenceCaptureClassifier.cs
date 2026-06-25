using AgentCommon;
using D2RAgent;

namespace D2RAgent.Tests;

public enum ReferenceVisibleState
{
    Unknown,
    DiabloSplash,
    ConnectingToBattleNet,
    CharacterScreen,
    OfflineCharacterScreen,
    LobbyOrGame,
    InGame
}

// Replicates VmOperations.DetectVisibleD2RState/DetectReadyScreenState's exact region
// definitions, sample grids, and priority order, but sampling a static reference capture
// (via FullCaptureRegionSampler) instead of a live D2R window through WindowsInput - the
// production code can't run outside a Windows host, so this is the closest thing to an
// end-to-end test of the real detection flow. If this drifts from VmOperations, that's a
// sign the production region/threshold logic changed and this needs to follow it.
internal static class ReferenceCaptureClassifier
{
    private const int MenuSampleGrid = 9;

    public static ReferenceVisibleState Classify(string capture)
    {
        if (IsDiabloSplashScreen(capture))
        {
            return IsConnectingToBattleNetDialog(capture)
                ? ReferenceVisibleState.ConnectingToBattleNet
                : ReferenceVisibleState.DiabloSplash;
        }

        if (IsCharacterScreenOffline(capture))
        {
            return ReferenceVisibleState.OfflineCharacterScreen;
        }

        if (IsCharacterScreenReady(capture))
        {
            return ReferenceVisibleState.CharacterScreen;
        }

        // Mirrors VmOperations.DetectVisibleD2RState: strict in-game evidence (HUD globes)
        // checked before the lobby check, since sitting_in_town.png proved the lobby-tab/
        // entry-button thresholds can coincidentally match ordinary outdoor scenery. The
        // broader Frame-kind fallback stays after the lobby check, unchanged from v0.2.64.
        if (IsInGameReadyStrict(capture))
        {
            return ReferenceVisibleState.InGame;
        }

        if (IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(capture))
        {
            return ReferenceVisibleState.LobbyOrGame;
        }

        return IsInGameReady(capture)
            ? ReferenceVisibleState.InGame
            : ReferenceVisibleState.Unknown;
    }

    public static bool IsDiabloSplashScreen(string capture)
    {
        var logo = Sample(capture, new UiPoint(0.500, 0.290), 0.45, 0.22);
        var prompt = Sample(capture, new UiPoint(0.500, 0.600), 0.32, 0.055);
        return D2RScreenClassifier.IsDiabloSplashScreen(logo, prompt);
    }

    public static bool IsConnectingToBattleNetDialog(string capture)
    {
        var dialog = Sample(capture, new UiPoint(0.500, 0.490), 0.30, 0.12);
        return D2RScreenClassifier.IsConnectingToBattleNetDialogRegion(dialog);
    }

    public static bool IsCharacterScreenOffline(string capture)
    {
        // Gated on the left-side character-select menu chrome (Diablo logo/Options/
        // Cinematics) being present first - without this, the empty-panel region alone
        // (a tall strip near the right edge) also reads dark/low-color on lobby and
        // in-game screens, which have their own dark decorative borders there.
        if (!IsCharacterMenuReady(capture))
        {
            return false;
        }

        var emptyPanel = Sample(capture, new UiPoint(0.895, 0.455), 0.17, 0.66);
        return D2RScreenClassifier.IsOfflineCharacterPanelRegion(emptyPanel);
    }

    public static bool IsCharacterScreenReady(string capture)
    {
        return IsCharacterButtonPairReady(capture) || IsCharacterMenuReady(capture);
    }

    public static bool IsCharacterButtonPairReady(string capture)
    {
        var play = Sample(capture, new UiPoint(0.420, 0.897), 0.13, 0.055);
        var lobby = Sample(capture, new UiPoint(0.585, 0.897), 0.13, 0.055);
        var characterList = Sample(capture, new UiPoint(0.890, 0.455), 0.17, 0.66);
        return D2RScreenClassifier.IsCharacterButtonRegion(play)
            && D2RScreenClassifier.IsCharacterButtonRegion(lobby)
            && D2RScreenClassifier.IsOnlineCharacterListRegion(characterList);
    }

    public static bool IsCharacterMenuReady(string capture)
    {
        var logo = Sample(capture, new UiPoint(0.105, 0.170), 0.13, 0.16);
        var options = Sample(capture, new UiPoint(0.105, 0.405), 0.13, 0.05);
        var cinematics = Sample(capture, new UiPoint(0.105, 0.460), 0.13, 0.05);
        return D2RScreenClassifier.IsCharacterMenuReady(logo, options, cinematics);
    }

    // Only the modern/legacy HUD globe profiles - never the broader Frame-kind fallback.
    // Mirrors VmOperations.IsInGameReadyStrict.
    public static bool IsInGameReadyStrict(string capture)
    {
        var actionHud = Sample(capture, new UiPoint(0.500, 0.955), 0.42, 0.08);
        var modernHealth = Sample(capture, new UiPoint(0.260, 0.900), 0.055, 0.080);
        var modernMana = Sample(capture, new UiPoint(0.760, 0.900), 0.055, 0.080);
        var legacyHealth = Sample(capture, new UiPoint(0.200, 0.900), 0.055, 0.080);
        var legacyMana = Sample(capture, new UiPoint(0.800, 0.900), 0.055, 0.080);

        return D2RScreenClassifier.IsInGameHudProfile(modernHealth, modernMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18)
            || D2RScreenClassifier.IsInGameHudProfile(legacyHealth, legacyMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18);
    }

    public static bool IsInGameReady(string capture)
    {
        var actionHud = Sample(capture, new UiPoint(0.500, 0.955), 0.42, 0.08);
        var modernHealth = Sample(capture, new UiPoint(0.260, 0.900), 0.055, 0.080);
        var modernMana = Sample(capture, new UiPoint(0.760, 0.900), 0.055, 0.080);
        var legacyHealth = Sample(capture, new UiPoint(0.200, 0.900), 0.055, 0.080);
        var legacyMana = Sample(capture, new UiPoint(0.800, 0.900), 0.055, 0.080);
        var bottomHud = Sample(capture, new UiPoint(0.500, 0.940), 0.70, 0.13);
        var centerHud = Sample(capture, new UiPoint(0.500, 0.940), 0.22, 0.08);

        if (D2RScreenClassifier.IsInGameHudProfile(modernHealth, modernMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return true;
        }

        if (D2RScreenClassifier.IsInGameHudProfile(legacyHealth, legacyMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return true;
        }

        return D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud);
    }

    public static bool IsAnyLobbyEntryMenuVisible(string capture)
    {
        if (IsInGameReady(capture) || IsCharacterScreenReady(capture) || IsCharacterScreenOffline(capture))
        {
            return false;
        }

        return IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(capture);
    }

    private static bool IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(string capture)
    {
        if (IsCharacterScreenReady(capture) || IsCharacterScreenOffline(capture))
        {
            return false;
        }

        var createTab = IsLobbyTabReady(capture, new UiPoint(0.673, 0.071));
        var joinTab = IsLobbyTabReady(capture, new UiPoint(0.766, 0.071));
        var entry = IsLobbyEntryButtonReady(capture);
        var formPanel = IsLobbyFormPanelReady(capture);
        return D2RScreenClassifier.IsGameEntryMenuVisible(createTab || joinTab, entry, formPanel);
    }

    private static bool IsLobbyTabReady(string capture, UiPoint tab)
    {
        var stats = Sample(capture, tab, 0.10, 0.045);
        return D2RScreenClassifier.IsLobbyTabReady(
            stats,
            IsCharacterButtonPairReady(capture),
            IsCharacterMenuReady(capture));
    }

    private static bool IsLobbyEntryButtonReady(string capture)
    {
        // CreateGameButton: 0.765,0.619 (automation-coordinate-catalog.md)
        var stats = Sample(capture, new UiPoint(0.765, 0.619), 0.16, 0.055);
        return D2RScreenClassifier.IsLobbyEntryButtonReady(stats);
    }

    private static bool IsLobbyFormPanelReady(string capture)
    {
        var stats = Sample(capture, new UiPoint(0.765, 0.365), 0.30, 0.42);
        return stats.AverageLuminance < 30
            && stats.GreyRatio < 0.25
            && stats.DarkRatio > 0.80;
    }

    private static ScreenRegionStats Sample(string capture, UiPoint center, double widthRatio, double heightRatio)
    {
        return FullCaptureRegionSampler.Sample(capture, center, widthRatio, heightRatio, MenuSampleGrid);
    }
}
