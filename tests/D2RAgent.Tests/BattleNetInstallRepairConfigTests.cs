using System.Text.Json;
using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class BattleNetInstallRepairConfigTests
{
    [Fact]
    public void NewConfigEnablesRepairWithTheStandardInstallDirectory()
    {
        var config = new VmAgentConfig();

        Assert.True(config.RepairBattleNetInstallLocationWhenNeeded);
        Assert.Equal(
            @"C:\Program Files (x86)\Diablo II Resurrected",
            config.D2RInstallDirectory);
    }

    [Fact]
    public void LegacyJsonWithoutRepairFieldsReceivesSafeDefaults()
    {
        var config = JsonSerializer.Deserialize<VmAgentConfig>(
            """
            {
              "agentId": "legacy-vm",
              "controllerUrl": "ws://controller/agent",
              "sharedSecret": "test-only"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(config);
        Assert.True(config.RepairBattleNetInstallLocationWhenNeeded);
        Assert.Equal(
            @"C:\Program Files (x86)\Diablo II Resurrected",
            config.D2RInstallDirectory);
    }

    [Fact]
    public void ExplicitRepairOptOutAndCustomDirectoryArePreserved()
    {
        var config = JsonSerializer.Deserialize<VmAgentConfig>(
            """
            {
              "repairBattleNetInstallLocationWhenNeeded": false,
              "d2rInstallDirectory": "D:\\Games\\Diablo II Resurrected"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(config);
        Assert.False(config.RepairBattleNetInstallLocationWhenNeeded);
        Assert.Equal(@"D:\Games\Diablo II Resurrected", config.D2RInstallDirectory);
    }
}
