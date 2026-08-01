using D2RAgent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace D2RAgent.Tests;

public sealed class GraphicsDeviceFailureDialogTests
{
    private const string FailureMessage =
        "Failed to initialize graphics device. Please ensure your PC meets the minimum system requirements and your drivers are up to date.";

    [Fact]
    public void SemanticMatcherReturnsTheStandardOkButton()
    {
        var dialog = CreateDialog();

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog);

        Assert.NotNull(match);
        Assert.Same(dialog, match.Dialog);
        Assert.NotNull(match.ActionButton);
        Assert.Equal(new IntPtr(103), match.ActionButton!.WindowHandle);
        Assert.Equal(1, match.ActionButton.ControlId);
    }

    [Fact]
    public void SemanticMatcherIgnoresMessageCaseAndWrapping()
    {
        var dialog = CreateDialog(
            processName: "D2R_5_ALT.exe",
            children:
            [
                new WindowControlSnapshot(
                    new IntPtr(102),
                    -1,
                    "Static",
                    "FAILED TO INITIALIZE GRAPHICS DEVICE.\r\nPlease ensure your drivers are up to date."),
                new WindowControlSnapshot(new IntPtr(103), 1, "button", "&OK")
            ]);

        Assert.NotNull(WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog));
    }

    // The failure this fixes: the message body is the evidence, and requiring the owning process,
    // the dialog class and the exact "Error" caption to all match first is what made a dialog we
    // could plainly read invisible to the agent.
    [Theory]
    [InlineData("notepad", "#32770", "Error")]
    [InlineData("D2R", "Dialog", "Error")]
    [InlineData("D2R", "#32770", "error")]
    [InlineData("D2R", "#32770", "Diablo II: Resurrected")]
    [InlineData("Diablo II Resurrected Launcher", "#32770", "")]
    public void SemanticMatcherAcceptsAnyOwnerClassAndCaptionCarryingTheMessage(
        string processName,
        string className,
        string title)
    {
        var dialog = CreateDialog(processName: processName, className: className, title: title);

        Assert.NotNull(WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog));
    }

    [Fact]
    public void SemanticMatcherRejectsUnrelatedD2RErrorMessage()
    {
        var dialog = CreateDialog(
            children:
            [
                new WindowControlSnapshot(new IntPtr(102), -1, "Static", "Failed to authenticate."),
                new WindowControlSnapshot(new IntPtr(103), 1, "Button", "OK")
            ]);

        Assert.Null(WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog));
    }

    [Fact]
    public void SemanticMatcherFallsBackToTheOkCaptionWhenIdOkIsMissing()
    {
        var dialog = CreateDialog(
            children:
            [
                new WindowControlSnapshot(new IntPtr(102), -1, "Static", FailureMessage),
                new WindowControlSnapshot(new IntPtr(103), 7, "Button", "&OK")
            ]);

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog);

        Assert.NotNull(match);
        Assert.Equal(new IntPtr(103), match.ActionButton!.WindowHandle);
    }

    // A dialog with no recognizable button is still a confirmed graphics-device failure. Reporting
    // it (with no action button) lets the caller fall back to WM_COMMAND/Enter and lets status say
    // what is on screen; treating it as "no match" is how a real failure went unseen.
    [Fact]
    public void SemanticMatcherReportsTheDialogEvenWithNoButtonToClick()
    {
        var dialog = CreateDialog(
            children: [new WindowControlSnapshot(new IntPtr(102), -1, "Static", FailureMessage)]);

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog);

        Assert.NotNull(match);
        Assert.Null(match.ActionButton);
    }

    [Fact]
    public void SemanticMatcherReadsTheMessageFromTheCaptionToo()
    {
        var dialog = CreateDialog(
            title: "D2R: failed to initialize graphics device",
            children: [new WindowControlSnapshot(new IntPtr(103), 1, "Button", "OK")]);

        Assert.NotNull(WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog));
    }

    [Fact]
    public void CroppedRunbookAssetHasPinnedDimensions()
    {
        using var image = Image.Load<Rgba32>(CapturePath("d2r_failed_to_initialize_graphics_device.png"));

        Assert.Equal(408, image.Width);
        Assert.Equal(157, image.Height);
    }

    private static WindowDialogSnapshot CreateDialog(
        string processName = "D2R",
        string className = "#32770",
        string title = "Error",
        WindowControlSnapshot[]? children = null)
    {
        return new WindowDialogSnapshot(
            ProcessId: 42,
            ProcessName: processName,
            SessionId: 1,
            WindowHandle: new IntPtr(101),
            ClassName: className,
            Title: title,
            Children: children ??
            [
                new WindowControlSnapshot(new IntPtr(102), -1, "Static", FailureMessage),
                new WindowControlSnapshot(new IntPtr(103), 1, "Button", "OK")
            ]);
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
