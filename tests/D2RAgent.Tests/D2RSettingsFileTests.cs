using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// The write half of the settings repair. Everything here is guarding one thing: a VM whose client
// works must never end up with a worse Settings.json than it started with.
public sealed class D2RSettingsFileTests
{
    [Fact]
    public void InvalidConfiguredPathsFailClosedWithoutEscapingPathExceptions()
    {
        Assert.Null(D2RSettingsFile.ResolveSettingsPath("bad\0path"));
        Assert.Null(D2RSettingsFile.ResolveRepairStatePath("bad\0path"));
        Assert.Equal(
            D2RSettingsRepairStateReadResult.Invalid,
            D2RSettingsFile.ReadRepairState("bad\0path", out _, out var error));
        Assert.Contains("Could not resolve", error, StringComparison.Ordinal);
    }

    // A known-good Settings.json on this fleet is 4 KB, and the operator's rule is that anything
    // under 2 KB is wrong - which is most of what the plausibility gate is enforcing, so the
    // fixture has to be realistically sized rather than a token three-key object. The bulk here is
    // key bindings, which is where the bulk of a real one is too.
    private static readonly string RealisticSettings = BuildSettings(bindings: 60);
    private const string CorruptShortSettings = """{"Display":{"Resolution":0},"Audio":{},"Gameplay":{}}""";

    [Fact]
    public void AReplacementKeepsABackupOfWhatWasThere()
    {
        using var settings = new TempSettings(CorruptShortSettings);

        Assert.True(D2RSettingsFile.TryReplace(settings.SettingsPath, RealisticSettings, out var backupPath, out var error));

        Assert.Equal("", error);
        Assert.Equal(RealisticSettings, File.ReadAllText(settings.SettingsPath));
        // The backup is the only surviving copy of what a corrupted file looked like, which is the
        // open question behind this whole feature - so it is kept even though (in fact, especially
        // because) the file it holds is one this gate would refuse to write.
        Assert.True(File.Exists(backupPath));
        Assert.Equal(CorruptShortSettings, File.ReadAllText(backupPath));
    }

    [Fact]
    public void RapidRetriesKeepEveryPriorFileInADistinctBackup()
    {
        using var settings = new TempSettings(CorruptShortSettings);
        var secondSettings = RealisticSettings.Replace("\"Gamma\": 1.0", "\"Gamma\": 1.2");

        Assert.True(D2RSettingsFile.TryReplace(
            settings.SettingsPath,
            RealisticSettings,
            out var firstBackup,
            out _));
        Assert.True(D2RSettingsFile.TryReplace(
            settings.SettingsPath,
            secondSettings,
            out var secondBackup,
            out _));

        Assert.NotEqual(firstBackup, secondBackup);
        Assert.Equal(CorruptShortSettings, File.ReadAllText(firstBackup));
        Assert.Equal(RealisticSettings, File.ReadAllText(secondBackup));
        Assert.Equal(secondSettings, File.ReadAllText(settings.SettingsPath));
        Assert.Equal(2, Directory.GetFiles(
            Path.GetDirectoryName(settings.SettingsPath)!,
            $"{D2RSettingsFile.SettingsFileName}.*.bak").Length);
    }

    // The operator's rule, measured on a real fleet file: a good one is 4 KB and anything under
    // 2 KB is wrong. This is the sharpest signal available - a truncated write, the suspected
    // cause of the whole problem, fails here even though it may still parse as JSON.
    [Theory]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(2047)]
    public void PayloadsUnderTwoKilobytesAreRejectedEvenWhenTheyParse(int length)
    {
        var undersized = BuildSettingsOfLength(length);

        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson(undersized, out var reason));
        Assert.Contains("corrupt or truncated", reason);
    }

    [Fact]
    public void TheSizeFloorIsTheOnlyThingRejectingAnUndersizedButOtherwiseValidFile()
    {
        // Same object either side of the boundary: 2047 fails, 2048 passes.
        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson(BuildSettingsOfLength(2047), out _));
        Assert.True(D2RSettingsFile.IsPlausibleSettingsJson(BuildSettingsOfLength(2048), out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void AFullSizeSettingsFileIsAccepted()
    {
        Assert.InRange(RealisticSettings.Length, 2 * 1024, 8 * 1024);
        Assert.True(D2RSettingsFile.IsPlausibleSettingsJson(RealisticSettings, out var reason));
        Assert.Equal("", reason);
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
    public void PreCommitRefusalLeavesTheDestinationUnchanged()
    {
        using var settings = new TempSettings(CorruptShortSettings);
        var directory = Path.GetDirectoryName(settings.SettingsPath)!;
        var sawFlushedTemporaryFile = false;
        var sawBackup = false;

        var replaced = D2RSettingsFile.TryReplace(
            settings.SettingsPath,
            RealisticSettings,
            out var backupPath,
            out var error,
            preCommitCheck: () =>
            {
                sawFlushedTemporaryFile = File.Exists($"{settings.SettingsPath}.d2rops-tmp");
                sawBackup = Directory.GetFiles(
                    directory,
                    $"{D2RSettingsFile.SettingsFileName}.*.bak").Length == 1;
                return (false, "A new D2R process generation appeared at commit.");
            });

        Assert.False(replaced);
        Assert.True(sawFlushedTemporaryFile);
        Assert.True(sawBackup);
        Assert.Contains("new D2R process generation", error);
        Assert.Equal(CorruptShortSettings, File.ReadAllText(settings.SettingsPath));
        Assert.True(File.Exists(backupPath));
        Assert.Equal(CorruptShortSettings, File.ReadAllText(backupPath));
        Assert.Empty(Directory.GetFiles(directory, "*.d2rops-tmp"));
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
    public void EmptyPayloadsAreRejected(string? content)
    {
        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson(content, out var reason));
        Assert.NotEqual("", reason);
    }

    // Full-size payloads that are not a settings file: prose, an array, a scalar, and an object
    // with a single enormous property. Each clears the 2 KB floor, so the JSON-shape checks are
    // what has to catch them.
    [Fact]
    public void FullSizePayloadsThatAreNotSettingsAreRejected()
    {
        var filler = new string('x', 3000);

        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson(filler, out var proseReason));
        Assert.Contains("not valid JSON", proseReason);

        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson($"\"{filler}\"", out var scalarReason));
        Assert.Contains("not an object", scalarReason);

        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson($"[\"{filler}\"]", out var arrayReason));
        Assert.Contains("not an object", arrayReason);

        Assert.False(D2RSettingsFile.IsPlausibleSettingsJson($"{{\"OneBigKey\":\"{filler}\"}}", out var thinReason));
        Assert.Contains("propert", thinReason);
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

    // Deliberately schema-free: D2R's key names are the game's business and change across patches,
    // so a full-size donor file using entirely different section names still has to be accepted.
    // Pinning the schema here would mean a game update silently disables repair fleet-wide.
    [Fact]
    public void AnUnfamiliarButWellFormedSettingsFileIsAccepted()
    {
        var unfamiliar = $$"""
            {
                "SomeFutureSection": { "NewKey": 1 },
                "AnotherSection": 2,
                "AndAThirdSection": "{{new string('v', 2500)}}"
            }
            """;

        Assert.True(D2RSettingsFile.IsPlausibleSettingsJson(unfamiliar, out var reason));
        Assert.Equal("", reason);
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
    public void RepairJournalReadDistinguishesMissingFromInvalid()
    {
        using var settings = new TempSettings(RealisticSettings);

        Assert.Equal(
            D2RSettingsRepairStateReadResult.Missing,
            D2RSettingsFile.ReadRepairState(settings.SettingsPath, out _, out _));

        var statePath = D2RSettingsFile.ResolveRepairStatePath(settings.SettingsPath)!;
        File.WriteAllText(statePath, "{ not-json");

        Assert.Equal(
            D2RSettingsRepairStateReadResult.Invalid,
            D2RSettingsFile.ReadRepairState(settings.SettingsPath, out _, out var error));
        Assert.Contains("Could not read", error);
    }

    [Fact]
    public void RepairJournalRequiresExplicitPhaseAndAttemptCount()
    {
        using var settings = new TempSettings(RealisticSettings);
        var statePath = D2RSettingsFile.ResolveRepairStatePath(settings.SettingsPath)!;
        File.WriteAllText(
            statePath,
            $$"""
              {
                "schemaVersion": {{D2RSettingsRepairState.CurrentSchemaVersion}}
              }
              """);

        Assert.Equal(
            D2RSettingsRepairStateReadResult.Invalid,
            D2RSettingsFile.ReadRepairState(settings.SettingsPath, out _, out var error));
        Assert.Contains("missing required properties", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSettingsPathHonoursAnExplicitOverride()
    {
        var configured = Path.Combine(Path.GetTempPath(), "d2rops-settings-override", "Settings.json");
        Assert.Equal(Path.GetFullPath(configured), D2RSettingsFile.ResolveSettingsPath(configured));
    }

    // Shaped like a real Settings.json - a few config sections plus a long key-bindings block,
    // which is where a real one's 4 KB mostly comes from.
    private static string BuildSettings(int bindings)
    {
        var keys = string.Join(
            ",\n        ",
            Enumerable.Range(1, bindings).Select(index =>
                $"\"Action{index:D2}\": {{ \"Primary\": \"Key{index:D2}\", \"Secondary\": \"Alt+Key{index:D2}\" }}"));

        return $$"""
            {
                "Display": { "Resolution": 1, "Fullscreen": true, "Gamma": 1.0, "Vsync": false },
                "Audio": { "MasterVolume": 0.5, "MusicVolume": 0.4, "EffectsVolume": 0.6 },
                "Gameplay": { "AlwaysRun": true, "ShowItemNames": true, "LegacyGraphics": false },
                "KeyBindings": {
                    {{keys}}
                }
            }
            """;
    }

    // An exact-length settings object that passes every check except size, so the size floor is
    // provably the only thing rejecting it - including at 2047, one character under the limit.
    private static string BuildSettingsOfLength(int length)
    {
        const string scaffold = """{"Display":{"Resolution":1},"Audio":{"MasterVolume":0.5},"KeyBindings":"@"}""";
        var overhead = scaffold.Length - 1;
        if (length <= overhead)
        {
            return scaffold.Replace("@", "");
        }

        var padded = scaffold.Replace("@", new string('x', length - overhead));
        Assert.Equal(length, padded.Length);
        return padded;
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
