using AgentCommon;
using D2RAgent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace D2RAgent.Tests;

public sealed class BattleNetScreenClassifierTests
{
    private const string InstallationRequired = "battlenet_installation_required.png";
    private const string InstallLanding = "battlenet_d2r_install_landing.png";
    private const string ShopLanding = "battlenet_shop_landing.png";
    private const string ChooseFolder = "battlenet_choose_install_folder.png";
    private const string StartInstall = "battlenet_start_install_scan.png";
    private const string InstalledPlay = "logged_in_battle_net.jpg";

    private static readonly D2RUiAutomationConfig Ui = new();

    public static TheoryData<string, bool> InstallationRequiredCases => new()
    {
        { InstallationRequired, true },
        { InstallLanding, false },
        { ShopLanding, false },
        { ChooseFolder, false },
        { StartInstall, false },
        { InstalledPlay, false }
    };

    public static TheoryData<string, bool> PrimaryActionCases => new()
    {
        // Play and Install deliberately share this coarse blue-button gate. Only the
        // two-button modal above authorizes repair, so an Install match alone is never enough
        // to click the dangerous primary action during repair.
        { InstalledPlay, true },
        { InstallLanding, true },
        { InstallationRequired, false },
        { ShopLanding, false },
        { ChooseFolder, false },
        { StartInstall, false }
    };

    public static TheoryData<string, bool> InstallConfirmationCases => new()
    {
        { StartInstall, true },
        { InstallationRequired, false },
        { InstallLanding, false },
        { ShopLanding, false },
        { ChooseFolder, false },
        { InstalledPlay, false }
    };

    [Theory]
    [MemberData(nameof(InstallationRequiredCases))]
    public void InstallationRequiredGateMatchesOnlyTheTwoButtonModal(
        string capture,
        bool expected)
    {
        var continueButton = SampleBattleNet(
            capture,
            D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton,
            widthRatio: 0.105,
            heightRatio: 0.050);
        var cancelButton = SampleBattleNet(
            capture,
            D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton,
            widthRatio: 0.090,
            heightRatio: 0.050);

        Assert.Equal(
            expected,
            BattleNetScreenClassifier.IsInstallationRequiredModal(continueButton, cancelButton));
    }

    [Theory]
    [MemberData(nameof(PrimaryActionCases))]
    public void PrimaryActionGateRecognizesBrightLauncherActionOnly(
        string capture,
        bool expected)
    {
        var action = SampleBattleNet(
            capture,
            D2RUiCoordinateTarget.BattleNetPlayButton,
            widthRatio: 0.160,
            heightRatio: 0.060);

        Assert.Equal(expected, BattleNetScreenClassifier.IsPrimaryActionReady(action));
    }

    [Theory]
    [MemberData(nameof(InstallConfirmationCases))]
    public void InstallConfirmationRequiresBothStartButtonAndTitle(
        string capture,
        bool expected)
    {
        var startButton = SampleBattleNet(
            capture,
            D2RUiCoordinateTarget.BattleNetStartInstallButton,
            widthRatio: 0.135,
            heightRatio: 0.050);
        var title = SampleBattleNet(
            capture,
            D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle,
            widthRatio: 0.360,
            heightRatio: 0.060);

        Assert.Equal(
            expected,
            BattleNetScreenClassifier.IsInstallLocationConfirmation(startButton, title));
    }

    [Fact]
    public void MissingSamplesNeverAuthorizeAnyLauncherAction()
    {
        var empty = ScreenRegionStatsCalculator.FromPixels([]);

        Assert.False(BattleNetScreenClassifier.IsInstallationRequiredModal(empty, empty));
        Assert.False(BattleNetScreenClassifier.IsPrimaryActionReady(empty));
        Assert.False(BattleNetScreenClassifier.IsInstallLocationConfirmation(empty, empty));
    }

    [Theory]
    [InlineData(InstallationRequired, 1066, 640)]
    [InlineData(InstallLanding, 1066, 640)]
    [InlineData(ShopLanding, 1066, 640)]
    [InlineData(ChooseFolder, 669, 473)]
    [InlineData(StartInstall, 1000, 641)]
    public void CroppedRunbookAssetHasPinnedDimensions(
        string capture,
        int expectedWidth,
        int expectedHeight)
    {
        using var image = Image.Load<Rgba32>(CapturePath(capture));

        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);
    }

    private static ScreenRegionStats SampleBattleNet(
        string capture,
        D2RUiCoordinateTarget target,
        double widthRatio,
        double heightRatio)
    {
        var point = D2RUiCoordinateCatalog.GetPoint(Ui, target);
        if (capture == InstalledPlay)
        {
            // The old reference includes the VMConnect toolbar. Battle.net's custom client
            // rectangle is x=0..1280, y=99..900 in that capture.
            return FullCaptureRegionSampler.SampleWithinBounds(
                capture,
                boundsLeft: 0,
                boundsTop: 99,
                boundsWidth: 1281,
                boundsHeight: 802,
                point,
                widthRatio,
                heightRatio);
        }

        return FullCaptureRegionSampler.Sample(capture, point, widthRatio, heightRatio);
    }

    private static string CapturePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "D2ROps.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
        }

        return Path.Combine(
            directory.FullName,
            "docs",
            "runbooks",
            "assets",
            "d2r-ui",
            "1366x768",
            fileName);
    }
}
