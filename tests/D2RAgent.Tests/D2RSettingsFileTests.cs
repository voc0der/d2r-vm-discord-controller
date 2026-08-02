using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// The write half of the settings repair. Everything here is guarding one thing: a VM whose client
// works must never end up with a worse Settings.json than it started with.
public sealed class D2RSettingsFileTests
{
    private const string RealisticSettings = """
        {
            "Display": { "Resolution": 1, "Fullscreen": true, "Gamma": 1.0 },
            "Audio": { "MasterVolume": 0.5 },
            "Gameplay": { "AlwaysRun": true }
        }
        """;

    [Fact]
    public void AReplacementKeepsABackupOfWhatWasThere()
    {
        using var settings = new TempSettings("""{"Display":{"Resolution":0},"Audio":{},"Gameplay":{}}""");

        Assert.True(D2RSettingsFile.TryReplace(settings.SettingsPath, RealisticSettings, out var backupPath, out var error));

        Assert.Equal("", error);
        Assert.Equal(RealisticSettings, File.ReadAllText(settings.SettingsPath));
        // The backup is the only surviving copy of what a corrupted file looked like, which is the
        // open question behind this whole feature.
        Assert.True(File.Exists(backupPath));
        Assert.Equal("""{"Display":{"Resolution":0},"Audio":{},"Gameplay":{}}""", File.ReadAllText(backupPath));
    }

    [Fact]
    public void AReplacementLeavesNoTemporaryFileBehind()
    {
        using var settings = new TempSettings(RealisticSettings);

        Assert.True(D2RSettingsFile.TryReplace(settings.SettingsPath, RealisticSettings, out _, out _));

        // The write is temp-file-then-rename precisely because a guest losing power mid-write is
        // the leading suspect for the corruption; a leftover temp file would mean the rename
        // never happened.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settings.SettingsPath)!, "*.d2rops-tmp"));
    }

    [Fact]
    public void ReplacingWritesTheFileWhenNoneExists()
    {
        using var settings = new TempSettings(contents: null);

        Assert.True(D2RSettingsFile.TryReplace(settings.SettingsPath, RealisticSettings, out var backupPath, out _));

        Assert.Equal(RealisticSettings, File.ReadAllText(settings.SettingsPath));
        Assert.Equal("", backupPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all, just a sentence that is long enough to clear the length floor.")]
    // Valid JSON, but not a settings file: an object with too little in it, an array, a scalar.
    [InlineData("""{"Display":{"Resolution":1},"Audio":{"MasterVolume":0.5}}""")]
    [InlineData("""[{"Display":1},{"Audio":2},{"Gameplay":3},{"Controls":4},{"Keys":5}]""")]
    [InlineData("\"a string long enough to pass the minimum content length check, but still a string\"")]
    public void ImplausiblePayloadsAreRejected(string? content)
    {
        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson(content, out var reason));
        Assert.NotEqual("", reason);
    }

    [Fact]
    public void AnImplausiblePayloadNeverTouchesTheExistingFile()
    {
        using var settings = new TempSettings(RealisticSettings);

        Assert.False(D2RSettingsFile.TryReplace(settings.SettingsPath, "{}", out var backupPath, out var error));

        Assert.Contains("Refusing", error);
        Assert.Equal("", backupPath);
        Assert.Equal(RealisticSettings, File.ReadAllText(settings.SettingsPath));
    }

    [Fact]
    public void ARealisticSettingsFileIsAccepted()
    {
        Assert.True(D2RSettingsFile.IsPlausibleSettingsJson(RealisticSettings, out var reason));
        Assert.Equal("", reason);
    }

    // Deliberately schema-free: D2R's key names are the game's business and change across patches,
    // so a donor file using entirely different section names still has to be accepted. Pinning the
    // schema here would mean a game update silently disables repair fleet-wide.
    [Fact]
    public void AnUnfamiliarButWellFormedSettingsFileIsAccepted()
    {
        Assert.True(D2RSettingsFile.IsPlausibleSettingsJson(
            """{"SomeFutureSection":{"NewKey":1},"AnotherSection":2,"AndAThirdSection":"value"}""",
            out _));
    }

    [Fact]
    public void ReadingReturnsContentAndAStableHash()
    {
        using var settings = new TempSettings(RealisticSettings);

        Assert.True(D2RSettingsFile.TryRead(settings.SettingsPath, out var snapshot, out _));

        Assert.Equal(RealisticSettings, snapshot.Content);
        Assert.Equal(D2RSettingsFile.Sha256(RealisticSettings), snapshot.Sha256);
        Assert.Equal(settings.SettingsPath, snapshot.Path);
    }

    // A donor whose own file is already garbage must fail the export rather than propagate it to
    // the one VM that is currently broken.
    [Fact]
    public void ReadingRefusesToDonateAnUnusableFile()
    {
        using var settings = new TempSettings("{}");

        Assert.False(D2RSettingsFile.TryRead(settings.SettingsPath, out _, out var error));
        Assert.Contains("not usable as a settings donor", error);
    }

    [Fact]
    public void ReadingAMissingFileFails()
    {
        using var settings = new TempSettings(contents: null);

        Assert.False(D2RSettingsFile.TryRead(settings.SettingsPath, out _, out var error));
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void AnUnresolvablePathFailsWithAnActionableMessage()
    {
        Assert.False(D2RSettingsFile.TryRead(null, out _, out var readError));
        Assert.Contains("d2rSettingsPath", readError);
        Assert.False(D2RSettingsFile.TryReplace(null, RealisticSettings, out _, out var writeError));
        Assert.Contains("d2rSettingsPath", writeError);
    }

    [Fact]
    public void ResolveSettingsPathHonoursAnExplicitOverride()
    {
        var configured = Path.Combine(Path.GetTempPath(), "d2rops-settings-override", "Settings.json");
        Assert.Equal(Path.GetFullPath(configured), D2RSettingsFile.ResolveSettingsPath(configured));
    }

    private sealed class TempSettings : IDisposable
    {
        private readonly string _directory;

        public TempSettings(string? contents)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"d2rops-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            SettingsPath = System.IO.Path.Combine(_directory, D2RSettingsFile.SettingsFileName);
            if (contents is not null)
            {
                File.WriteAllText(SettingsPath, contents);
            }
        }

        public string SettingsPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
