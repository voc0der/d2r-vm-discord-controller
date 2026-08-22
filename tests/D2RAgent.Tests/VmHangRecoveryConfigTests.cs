using System.Text.Json;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The defaults here decide whether a healthy-but-slow VM can have its power cut, so they are
// pinned rather than left to drift, and the validation rules that keep an operator from
// configuring an unsafe window are exercised directly.
public sealed class VmHangRecoveryConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [Fact]
    public void DefaultsArePatientEnoughToClearAColdBoot()
    {
        var config = new VmHangRecoveryConfig();

        Assert.True(config.Enabled);
        Assert.Equal(300, config.HangSuspectedAfterSeconds);
        Assert.Equal(1200, config.NoEvidenceGraceSeconds);
        Assert.Equal(10, config.SettleSeconds);
        Assert.Equal(2, config.MaxHardPowerCuts);

        // The no-evidence path must never be quicker to act than the one backed by a real
        // heartbeat reading, or an un-updated worker node would be the most trigger-happy.
        Assert.True(config.NoEvidenceGraceSeconds >= config.HangSuspectedAfterSeconds);
    }

    // A host config written before this feature existed has no vmHangRecovery object at all, and
    // must come up with the defaults rather than a zeroed-out window that would cut power instantly.
    [Fact]
    public void AConfigWithoutTheSectionGetsTheDefaults()
    {
        var config = JsonSerializer.Deserialize<HostConfig>("""{"nodeId":"local"}""", Options);

        Assert.NotNull(config);
        Assert.True(config!.VmHangRecovery.Enabled);
        Assert.Equal(300, config.VmHangRecovery.HangSuspectedAfterSeconds);
        Assert.Equal(2, config.VmHangRecovery.MaxHardPowerCuts);
    }

    // The validation guards, exercised through the real loader. Each one exists to stop an
    // operator configuring a window that would power-cut healthy VMs.
    [Theory]
    [InlineData("""{"enabled":true,"hangSuspectedAfterSeconds":30,"noEvidenceGraceSeconds":1200,"settleSeconds":10,"maxHardPowerCuts":2}""", "hangSuspectedAfterSeconds")]
    [InlineData("""{"enabled":true,"hangSuspectedAfterSeconds":600,"noEvidenceGraceSeconds":300,"settleSeconds":10,"maxHardPowerCuts":2}""", "noEvidenceGraceSeconds")]
    [InlineData("""{"enabled":true,"hangSuspectedAfterSeconds":300,"noEvidenceGraceSeconds":1200,"settleSeconds":0,"maxHardPowerCuts":2}""", "settleSeconds")]
    [InlineData("""{"enabled":true,"hangSuspectedAfterSeconds":300,"noEvidenceGraceSeconds":1200,"settleSeconds":10,"maxHardPowerCuts":0}""", "maxHardPowerCuts")]
    [InlineData("""{"enabled":true,"hangSuspectedAfterSeconds":300,"noEvidenceGraceSeconds":1200,"settleSeconds":10,"maxHardPowerCuts":9}""", "maxHardPowerCuts")]
    public void UnsafeWindowsAreRefusedAtLoad(string section, string expectedField)
    {
        var path = WriteConfigWithHangRecovery(section);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => HostConfigLoader.Load(path));
            Assert.Contains(expectedField, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheShippedDefaultsLoadCleanly()
    {
        var path = WriteConfigWithHangRecovery(
            """{"enabled":true,"hangSuspectedAfterSeconds":300,"noEvidenceGraceSeconds":1200,"settleSeconds":10,"maxHardPowerCuts":2}""");
        try
        {
            var config = HostConfigLoader.Load(path);
            Assert.Equal(300, config.VmHangRecovery.HangSuspectedAfterSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteConfigWithHangRecovery(string section)
    {
        var path = Path.Combine(Path.GetTempPath(), $"d2r-host-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
        {
          "mode": "master",
          "nodeId": "local",
          "discordToken": "x",
          "disableDiscord": true,
          "databasePath": "test.sqlite",
          "vmHangRecovery": {{section}}
        }
        """);
        return path;
    }

    [Fact]
    public void TheSectionRoundTripsFromJson()
    {
        var config = JsonSerializer.Deserialize<HostConfig>(
            """{"vmHangRecovery":{"enabled":false,"hangSuspectedAfterSeconds":600,"noEvidenceGraceSeconds":1800,"settleSeconds":30,"maxHardPowerCuts":1}}""",
            Options);

        Assert.NotNull(config);
        Assert.False(config!.VmHangRecovery.Enabled);
        Assert.Equal(600, config.VmHangRecovery.HangSuspectedAfterSeconds);
        Assert.Equal(1800, config.VmHangRecovery.NoEvidenceGraceSeconds);
        Assert.Equal(30, config.VmHangRecovery.SettleSeconds);
        Assert.Equal(1, config.VmHangRecovery.MaxHardPowerCuts);
    }
}
