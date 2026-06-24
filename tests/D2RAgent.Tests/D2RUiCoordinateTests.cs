using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class D2RUiCoordinateTests
{
    public static TheoryData<D2RUiCoordinateTarget, int, int> BaselineTargets => new()
    {
        { D2RUiCoordinateTarget.BattleNetPlayButton, 176, 540 },
        { D2RUiCoordinateTarget.BattleNetWhatsNewTitle, 309, 144 },
        { D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton, 1152, 112 },
        { D2RUiCoordinateTarget.IntroSkipPoint, 683, 384 },
        { D2RUiCoordinateTarget.CharacterSlot1, 1216, 92 },
        { D2RUiCoordinateTarget.CharacterSlot2, 1216, 161 },
        { D2RUiCoordinateTarget.CharacterSlot3, 1216, 230 },
        { D2RUiCoordinateTarget.CharacterSlot4, 1216, 292 },
        { D2RUiCoordinateTarget.CharacterSlot5, 1216, 361 },
        { D2RUiCoordinateTarget.CharacterSlot6, 1216, 430 },
        { D2RUiCoordinateTarget.CharacterSlot7, 1216, 499 },
        { D2RUiCoordinateTarget.CharacterSlot8, 1216, 568 },
        { D2RUiCoordinateTarget.CharacterPlayButton, 574, 689 },
        { D2RUiCoordinateTarget.CharacterLobbyButton, 799, 689 },
        { D2RUiCoordinateTarget.CharacterOnlineTab, 1161, 38 },
        { D2RUiCoordinateTarget.LobbyPartyIcon, 131, 543 },
        { D2RUiCoordinateTarget.FriendRowStart, 246, 138 },
        { D2RUiCoordinateTarget.FriendContextJoinGame, 434, 247 },
        { D2RUiCoordinateTarget.JoinGameTab, 1046, 55 },
        { D2RUiCoordinateTarget.JoinGameNameField, 952, 106 },
        { D2RUiCoordinateTarget.JoinPasswordField, 1143, 106 },
        { D2RUiCoordinateTarget.JoinDifficultyDropdown, 1153, 147 },
        { D2RUiCoordinateTarget.JoinDifficultyNormalOption, 1153, 169 },
        { D2RUiCoordinateTarget.JoinDifficultyNightmareOption, 1153, 196 },
        { D2RUiCoordinateTarget.JoinDifficultyHellOption, 1153, 223 },
        { D2RUiCoordinateTarget.JoinGameButton, 1045, 478 },
        { D2RUiCoordinateTarget.GameEntryErrorDialogOkButton, 683, 414 },
        { D2RUiCoordinateTarget.CreateGameTab, 919, 55 },
        { D2RUiCoordinateTarget.CreateGameNameField, 1046, 123 },
        { D2RUiCoordinateTarget.CreatePasswordField, 1046, 172 },
        { D2RUiCoordinateTarget.CreateNormalButton, 952, 269 },
        { D2RUiCoordinateTarget.CreateNightmareButton, 1048, 269 },
        { D2RUiCoordinateTarget.CreateHellButton, 1137, 269 },
        { D2RUiCoordinateTarget.CreateGameButton, 1045, 475 },
        { D2RUiCoordinateTarget.SaveAndExitButton, 683, 337 },
        { D2RUiCoordinateTarget.ModernHealthGlobe, 355, 691 },
        { D2RUiCoordinateTarget.ModernManaGlobe, 1038, 691 },
        { D2RUiCoordinateTarget.LegacyHealthGlobe, 273, 691 },
        { D2RUiCoordinateTarget.LegacyManaGlobe, 1093, 691 },
        { D2RUiCoordinateTarget.InGameHudBar, 683, 733 }
    };

    [Theory]
    [MemberData(nameof(BaselineTargets))]
    public void CatalogDefaultsHaveExpectedBaselinePixels(D2RUiCoordinateTarget target, int expectedX, int expectedY)
    {
        var point = D2RUiCoordinateCatalog.GetPoint(new D2RUiAutomationConfig(), target);
        var pixels = D2RUiCoordinateCatalog.ToBaselinePixels(point);

        Assert.Equal(new UiPixelPoint(expectedX, expectedY), pixels);
    }

    [Theory]
    [InlineData("Create Game", 963, 454, 1128, 496)]
    [InlineData("Join Game", 963, 460, 1128, 502)]
    public void FinalEntryButtonsLandInsideBaselineCaptureButton(string buttonName, int minX, int minY, int maxX, int maxY)
    {
        var point = buttonName == "Create Game"
            ? D2RUiCoordinateCatalog.GetPoint(new D2RUiAutomationConfig(), D2RUiCoordinateTarget.CreateGameButton)
            : D2RUiCoordinateCatalog.GetPoint(new D2RUiAutomationConfig(), D2RUiCoordinateTarget.JoinGameButton);

        var pixels = D2RUiCoordinateCatalog.ToBaselinePixels(point);

        Assert.InRange(pixels.X, minX, maxX);
        Assert.InRange(pixels.Y, minY, maxY);
    }

    [Fact]
    public void CatalogFallsBackWhenConfiguredPointIsInvalid()
    {
        var ui = new D2RUiAutomationConfig
        {
            CharacterLobbyButton = new UiPoint(double.NaN, 2)
        };

        var pixels = D2RUiCoordinateCatalog.ToBaselinePixels(
            D2RUiCoordinateCatalog.GetPoint(ui, D2RUiCoordinateTarget.CharacterLobbyButton));

        Assert.Equal(new UiPixelPoint(799, 689), pixels);
    }

    [Fact]
    public void CharacterSlotHelperFallsBackWhenSlotArrayIsIncomplete()
    {
        var ui = new D2RUiAutomationConfig
        {
            CharacterSlots = [new UiPoint(0.5, 0.5)]
        };

        var pixels = D2RUiCoordinateCatalog.ToBaselinePixels(
            D2RUiCoordinateCatalog.GetCharacterSlotPoint(ui, 3));

        Assert.Equal(new UiPixelPoint(1216, 230), pixels);
    }

    [Fact]
    public void FriendRowHelperUsesConfiguredRowHeightWithBaselinePixels()
    {
        var row2 = D2RUiCoordinateCatalog.GetFriendRowPoint(new D2RUiAutomationConfig(), 2);
        var row3 = D2RUiCoordinateCatalog.GetFriendRowPoint(new D2RUiAutomationConfig(), 3);

        Assert.Equal(new UiPixelPoint(246, 176), D2RUiCoordinateCatalog.ToBaselinePixels(row2));
        Assert.Equal(new UiPixelPoint(246, 214), D2RUiCoordinateCatalog.ToBaselinePixels(row3));
    }

    [Fact]
    public void ExplicitInvalidCharacterSlotStillFailsClearly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            D2RUiCoordinateCatalog.GetCharacterSlotPoint(new D2RUiAutomationConfig(), 99));

        Assert.Contains("Character slot must be between 1 and 8", error.Message);
    }

    [Fact]
    public void ExplicitInvalidFriendRowStillFailsClearly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            D2RUiCoordinateCatalog.GetFriendRowPoint(new D2RUiAutomationConfig(), 0));

        Assert.Contains("friendRow must be 1 or greater", error.Message);
    }

    [Fact]
    public void CatalogDocumentsEveryTarget()
    {
        var coordinates = D2RUiCoordinateCatalog.GetAll(new D2RUiAutomationConfig());

        Assert.Equal(Enum.GetValues<D2RUiCoordinateTarget>().Length, coordinates.Count);
        Assert.All(coordinates, coordinate =>
        {
            Assert.NotEmpty(coordinate.Label);
            Assert.InRange(coordinate.BaselinePixels.X, 0, D2RUiCoordinateCatalog.BaselineWidth);
            Assert.InRange(coordinate.BaselinePixels.Y, 0, D2RUiCoordinateCatalog.BaselineHeight);
        });
    }
}
