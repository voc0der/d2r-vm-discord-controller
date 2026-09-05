using System.Text.Json;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The windows here decide how long a guest may be silent before the host power-cycles it, so they
// are pinned rather than left to drift, and the guards that stop an operator configuring an
// impatient one are exercised through the real loader.
public sealed class StuckVmWatchdogConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [Fact]
    public void DefaultsClearAColdBootWithRoomToSpare()
    {
        var config = new StuckVmWatchdogConfig();

        Assert.True(config.Enabled);
        Assert.Equal(240, config.AgentOfflineGraceSeconds);
        Assert.Equal(600, config.NoHeartbeatEvidenceGraceSeconds);
        Assert.Equal(180, config.MinimumVmUptimeSeconds);
        Assert.Equal(2, config.MaxRecoveriesPerVm);
        Assert.Equal(60, config.SweepIntervalSeconds);

        // An uncorroborated streak must never act sooner than one a heartbeat reading backs, or a
        // VM with the integration service switched off would be the most trigger-happy in the fleet.
        Assert.True(config.NoHeartbeatEvidenceGraceSeconds >= config.AgentOfflineGraceSeconds);
    }

    // A host config written before this feature existed has no stuckVmWatchdog object, and must come
    // up with the defaults rather than a zeroed-out window that would cycle every VM immediately.
    [Fact]
    public void AConfigWithoutTheSectionGetsTheDefaults()
    {
        var config = JsonSerializer.Deserialize<HostConfig>("""{"nodeId":"local"}""", Options);

        Assert.NotNull(config);
        Assert.True(config!.StuckVmWatchdog.Enabled);
        Assert.Equal(240, config.StuckVmWatchdog.AgentOfflineGraceSeconds);
        Assert.Equal(180, config.StuckVmWatchdog.MinimumVmUptimeSeconds);
        Assert.Equal(2, config.StuckVmWatchdog.MaxRecoveriesPerVm);
    }

    [Theory]
    [InlineData("""{"agentOfflineGraceSeconds":30}""", "agentOfflineGraceSeconds")]
    [InlineData("""{"agentOfflineGraceSeconds":600,"noHeartbeatEvidenceGraceSeconds":300}""", "noHeartbeatEvidenceGraceSeconds")]
    [InlineData("""{"minimumVmUptimeSeconds":10}""", "minimumVmUptimeSeconds")]
    [InlineData("""{"maxRecoveriesPerVm":0}""", "maxRecoveriesPerVm")]
    [InlineData("""{"maxRecoveriesPerVm":9}""", "maxRecoveriesPerVm")]
    [InlineData("""{"sweepIntervalSeconds":5}""", "sweepIntervalSeconds")]
    [InlineData("""{"sweepIntervalSeconds":7200}""", "sweepIntervalSeconds")]
    public void ImpatientWindowsAreRefusedAtLoad(string section, string expectedField)
    {
        var path = WriteConfigWithWatchdog(section);
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
        var path = WriteConfigWithWatchdog(
            """{"enabled":true,"agentOfflineGraceSeconds":240,"noHeartbeatEvidenceGraceSeconds":600,"minimumVmUptimeSeconds":180,"maxRecoveriesPerVm":2,"sweepIntervalSeconds":60}""");
        try
        {
            var config = HostConfigLoader.Load(path);
            Assert.Equal(240, config.StuckVmWatchdog.AgentOfflineGraceSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Turning it off has to be reachable without also having to restate every window, because the
    // operator reaching for it is doing so while something is going wrong.
    [Fact]
    public void ItCanBeDisabledWithoutRestatingEveryWindow()
    {
        var path = WriteConfigWithWatchdog("""{"enabled":false}""");
        try
        {
            var config = HostConfigLoader.Load(path);
            Assert.False(config.StuckVmWatchdog.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteConfigWithWatchdog(string section)
    {
        var path = Path.Combine(Path.GetTempPath(), $"d2r-host-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
        {
          "mode": "master",
          "nodeId": "local",
          "discordToken": "x",
          "disableDiscord": true,
          "databasePath": "test.sqlite",
          "stuckVmWatchdog": {{section}}
        }
        """);
        return path;
    }

    [Fact]
    public void TheSectionRoundTripsFromJson()
    {
        var config = JsonSerializer.Deserialize<HostConfig>(
            """{"stuckVmWatchdog":{"enabled":false,"agentOfflineGraceSeconds":600,"noHeartbeatEvidenceGraceSeconds":1800,"minimumVmUptimeSeconds":300,"maxRecoveriesPerVm":1,"sweepIntervalSeconds":120}}""",
            Options);

        Assert.NotNull(config);
        Assert.False(config!.StuckVmWatchdog.Enabled);
        Assert.Equal(600, config.StuckVmWatchdog.AgentOfflineGraceSeconds);
        Assert.Equal(1800, config.StuckVmWatchdog.NoHeartbeatEvidenceGraceSeconds);
        Assert.Equal(300, config.StuckVmWatchdog.MinimumVmUptimeSeconds);
        Assert.Equal(1, config.StuckVmWatchdog.MaxRecoveriesPerVm);
        Assert.Equal(120, config.StuckVmWatchdog.SweepIntervalSeconds);
    }
}
