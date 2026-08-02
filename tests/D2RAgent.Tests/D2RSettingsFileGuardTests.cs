using System.Text.Json;
using AgentCommon;
using D2RAgent;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// D2R rewrites Settings.json on exit and regenerates it at defaults when it decides the file is
// unusable, which silently wipes the resolution/graphics setup the pixel classifiers are calibrated
// against. These cover the guard that locks the file at agent startup, and the status surface that
// makes a failed lock visible instead of leaving the VM looking healthy.
public sealed class D2RSettingsFileGuardTests
{
    [Fact]
    public void EnsureReadOnly_MarksAWritableFileReadOnly()
    {
        using var settings = new TempSettingsFile("{\"resolution\":1}");

        var result = D2RSettingsFileGuard.EnsureReadOnly(settings.SettingsPath);

        Assert.Equal(D2RSettingsProtectionOutcome.MadeReadOnly, result.Outcome);
        Assert.True(result.ReadOnly);
        Assert.True(result.Ok);
        Assert.Equal(settings.SettingsPath, result.SettingsPath);
        Assert.True(File.GetAttributes(settings.SettingsPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void EnsureReadOnly_LeavesAnAlreadyLockedFileAlone()
    {
        using var settings = new TempSettingsFile("{\"resolution\":1}");
        File.SetAttributes(settings.SettingsPath, File.GetAttributes(settings.SettingsPath) | FileAttributes.ReadOnly);
        var writtenAt = File.GetLastWriteTimeUtc(settings.SettingsPath);

        var result = D2RSettingsFileGuard.EnsureReadOnly(settings.SettingsPath);

        Assert.Equal(D2RSettingsProtectionOutcome.AlreadyReadOnly, result.Outcome);
        Assert.True(result.Ok);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(settings.SettingsPath));
    }

    [Fact]
    public void EnsureReadOnly_LockedFileRejectsWrites()
    {
        // On Unix the read-only attribute is just the write permission bits, which root ignores;
        // Windows enforces the attribute against everyone, elevated included.
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var settings = new TempSettingsFile("{\"resolution\":1}");

        D2RSettingsFileGuard.EnsureReadOnly(settings.SettingsPath);

        // The whole point of the guard: D2R's own rewrite has to fail.
        Assert.ThrowsAny<UnauthorizedAccessException>(
            () => File.WriteAllText(settings.SettingsPath, "{\"resolution\":0}"));
        Assert.Equal("{\"resolution\":1}", File.ReadAllText(settings.SettingsPath));
    }

    [Fact]
    public void EnsureReadOnly_ReportsAMissingFileInsteadOfCreatingOne()
    {
        using var settings = new TempSettingsFile(contents: null);

        var result = D2RSettingsFileGuard.EnsureReadOnly(settings.SettingsPath);

        Assert.Equal(D2RSettingsProtectionOutcome.Missing, result.Outcome);
        Assert.False(result.Ok);
        // Creating a placeholder here would lock in the exact default-settings state being
        // defended against.
        Assert.False(File.Exists(settings.SettingsPath));
    }

    [Fact]
    public void ResolveSettingsPath_PrefersTheConfiguredPath()
    {
        var configured = Path.Combine(Path.GetTempPath(), "d2rops-settings-override", "Settings.json");

        var resolved = D2RSettingsFileGuard.ResolveSettingsPath(configured);

        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void ResolveSettingsPath_ExpandsEnvironmentVariables()
    {
        // The real path is under %USERPROFILE%, so an operator overriding it is likely to write
        // it the same way.
        var root = Path.Combine(Path.GetTempPath(), "d2rops-settings-env");
        Environment.SetEnvironmentVariable("D2ROPS_TEST_ROOT", root);
        try
        {
            var resolved = D2RSettingsFileGuard.ResolveSettingsPath(
                Path.Combine("%D2ROPS_TEST_ROOT%", "Settings.json"));

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "Settings.json")), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("D2ROPS_TEST_ROOT", null);
        }
    }

    [Fact]
    public void Protect_HonoursTheConfigOptOut()
    {
        using var settings = new TempSettingsFile("{\"resolution\":1}");
        var config = new VmAgentConfig { ProtectD2RSettings = false, D2RSettingsPath = settings.SettingsPath };

        var result = D2RSettingsFileGuard.Protect(config);

        Assert.Equal(D2RSettingsProtectionOutcome.Skipped, result.Outcome);
        Assert.False(File.GetAttributes(settings.SettingsPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void EnsureD2RSettingsProtected_RunsOnceAndOnlyOnce()
    {
        using var settings = new TempSettingsFile("{\"resolution\":1}");
        var operations = new VmOperations(new VmAgentConfig { D2RSettingsPath = settings.SettingsPath });

        var first = operations.EnsureD2RSettingsProtected();
        // Clearing the attribute mimics anything that unlocks the file after startup: the second
        // call must report the original result rather than silently re-locking, so "first load
        // only" holds however many callers there are.
        File.SetAttributes(settings.SettingsPath, File.GetAttributes(settings.SettingsPath) & ~FileAttributes.ReadOnly);
        var second = operations.EnsureD2RSettingsProtected();

        Assert.Equal(D2RSettingsProtectionOutcome.MadeReadOnly, first.Outcome);
        Assert.Same(first, second);
        Assert.False(File.GetAttributes(settings.SettingsPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Status_CarriesTheProtectionResult()
    {
        using var settings = new TempSettingsFile("{\"resolution\":1}");
        var operations = new VmOperations(new VmAgentConfig { D2RSettingsPath = settings.SettingsPath });
        operations.EnsureD2RSettingsProtected();

        var json = JsonSerializer.Serialize(await operations.GetStatusAsync(CancellationToken.None));

        using var document = JsonDocument.Parse(json);
        var protection = document.RootElement.GetProperty("d2rSettingsProtection");
        Assert.Equal("MadeReadOnly", protection.GetProperty("outcome").GetString());
        Assert.True(protection.GetProperty("readOnly").GetBoolean());
        Assert.Equal(settings.SettingsPath, protection.GetProperty("path").GetString());
    }

    [Fact]
    public void HostStatusLine_StaysQuietWhenTheFileIsLocked()
    {
        var json = BuildStatusJson(outcome: "MadeReadOnly", readOnly: true);

        Assert.False(DiscordBot.TryReadUnprotectedSettingsSummary(json, out _));
    }

    [Fact]
    public void HostStatusLine_StaysQuietWhenTheGuardIsTurnedOff()
    {
        var json = BuildStatusJson(outcome: "Skipped", readOnly: false);

        Assert.False(DiscordBot.TryReadUnprotectedSettingsSummary(json, out _));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Missing")]
    public void HostStatusLine_CallsOutAnUnlockedSettingsFile(string outcome)
    {
        var json = BuildStatusJson(outcome, readOnly: false);

        Assert.True(DiscordBot.TryReadUnprotectedSettingsSummary(json, out var summary));
        Assert.Contains("NOT LOCKED", summary);
        Assert.Contains(outcome, summary);
        Assert.Contains("something went wrong", summary);
    }

    [Fact]
    public void HostStatusLine_StaysQuietForAgentsThatDoNotReportProtection()
    {
        // Satellites running a build from before this field exists must not read as failures.
        Assert.False(DiscordBot.TryReadUnprotectedSettingsSummary("{\"d2rRunning\":true}", out _));
        Assert.False(DiscordBot.TryReadUnprotectedSettingsSummary("not json", out _));
        Assert.False(DiscordBot.TryReadUnprotectedSettingsSummary(null, out _));
    }

    private static string BuildStatusJson(string outcome, bool readOnly)
    {
        return JsonSerializer.Serialize(new
        {
            d2rRunning = true,
            d2rSettingsProtection = new
            {
                outcome,
                ok = readOnly,
                path = @"C:\Users\d2r\Saved Games\Diablo II Resurrected\Settings.json",
                readOnly,
                message = "something went wrong"
            }
        });
    }

    private sealed class TempSettingsFile : IDisposable
    {
        private readonly string _directory;

        public TempSettingsFile(string? contents)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"d2rops-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            SettingsPath = System.IO.Path.Combine(_directory, D2RSettingsFileGuard.SettingsFileName);
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
                if (File.Exists(SettingsPath))
                {
                    File.SetAttributes(SettingsPath, File.GetAttributes(SettingsPath) & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
