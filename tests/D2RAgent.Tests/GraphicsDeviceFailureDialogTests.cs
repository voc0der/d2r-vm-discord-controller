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

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog, ["D2R"]);

        Assert.NotNull(match);
        Assert.Same(dialog, match.Dialog);
        Assert.Equal(new IntPtr(103), match.ActionButton.WindowHandle);
        Assert.Equal(1, match.ActionButton.ControlId);
    }

    [Fact]
    public void SemanticMatcherNormalizesConfiguredOwnerAndIgnoresMessageCaseAndWrapping()
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

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(
            dialog,
            [@"C:\Games\D2R_5_ALT.exe"]);

        Assert.NotNull(match);
    }

    [Fact]
    public void SemanticMatcherRejectsGenericErrorFromAnotherProcess()
    {
        var dialog = CreateDialog(processName: "notepad");

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog, ["D2R"]);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("Dialog", "Error")]
    [InlineData("#32770", "error")]
    [InlineData("#32770", "Warning")]
    public void SemanticMatcherRequiresStandardDialogClassAndExactErrorTitle(
        string className,
        string title)
    {
        var dialog = CreateDialog(className: className, title: title);

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog, ["D2R"]);

        Assert.Null(match);
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

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog, ["D2R"]);

        Assert.Null(match);
    }

    [Theory]
    [InlineData(2, "Button")]
    [InlineData(1, "Static")]
    public void SemanticMatcherRequiresButtonClassWithIdOk(int controlId, string className)
    {
        var dialog = CreateDialog(
            children:
            [
                new WindowControlSnapshot(new IntPtr(102), -1, "Static", FailureMessage),
                new WindowControlSnapshot(new IntPtr(103), controlId, className, "OK")
            ]);

        var match = WindowsProcessFinder.MatchD2RGraphicsDeviceFailureDialog(dialog, ["D2R"]);

        Assert.Null(match);
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
