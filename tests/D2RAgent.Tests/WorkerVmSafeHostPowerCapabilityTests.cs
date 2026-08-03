using System.Text.Json;
using AgentCommon;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class WorkerVmSafeHostPowerCapabilityTests
{
    [Theory]
    [InlineData("{\"vmSafeHostPowerTransitions\":true}", true)]
    [InlineData("{\"vmSafeHostPowerTransitions\":false}", false)]
    [InlineData("{}", false)]
    [InlineData("{\"vmSafeHostPowerTransitions\":\"true\"}", false)]
    [InlineData("{\"vmSafeHostPowerTransitions\":1}", false)]
    public void CapabilityParserRequiresAnExplicitJsonTrue(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            expected,
            FleetRegistry.ReadBooleanCapability(
                document.RootElement,
                "vmSafeHostPowerTransitions"));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, false, false)]
    public void CapabilityOnlyAuthorizesTheCurrentConnectedStatusFrame(
        bool connected,
        bool hasConnectionGeneration,
        bool statusReceivedOnCurrentConnection,
        bool advertisedCapability,
        bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new AgentSnapshot(
            "server-b",
            "host",
            "Server B",
            "server-b-host",
            "development-build",
            connected,
            ConnectedAt: hasConnectionGeneration ? now : null,
            LastSeenAt: now,
            LastStatusJson: "{\"vmSafeHostPowerTransitions\":true}",
            StatusReceivedAt: statusReceivedOnCurrentConnection ? now : null);

        Assert.Equal(
            expected,
            FleetRegistry.IsCurrentWorkerVmSafeHostPowerCapable(
                worker,
                advertisedCapability));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, false, false)]
    public void GenerationBoundCommandsRequireCapabilityFromTheCurrentWorkerConnection(
        bool connected,
        bool hasConnectionGeneration,
        bool statusReceivedOnCurrentConnection,
        bool advertisedCapability,
        bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new AgentSnapshot(
            "server-b",
            "host",
            "Server B",
            "server-b-host",
            "development-build",
            connected,
            ConnectedAt: hasConnectionGeneration ? now : null,
            LastSeenAt: now,
            LastStatusJson: "{\"generationBoundAgentCommands\":true}",
            StatusReceivedAt: statusReceivedOnCurrentConnection ? now : null);

        Assert.Equal(
            expected,
            FleetRegistry.IsCurrentWorkerGenerationBoundAgentCommandsCapable(
                worker,
                advertisedCapability));
    }

    [Theory]
    [InlineData(HostSystemPowerAction.Sleep)]
    [InlineData(HostSystemPowerAction.Shutdown)]
    [InlineData(HostSystemPowerAction.Restart)]
    public void EveryRemoteHostPowerActionFailsClosedWithoutTheCapability(
        HostSystemPowerAction action)
    {
        var reason = FleetHostOperations.GetVmSafeHostPowerCapabilityBlockReason(
            "server-b",
            action,
            capabilityReceivedOnCurrentConnection: false);

        Assert.NotNull(reason);
        Assert.Contains("server-b", reason, StringComparison.Ordinal);
        Assert.Contains("VM-safe host power transitions", reason, StringComparison.Ordinal);
        Assert.Contains("current connection", reason, StringComparison.Ordinal);
        Assert.Contains("was not queued", reason, StringComparison.Ordinal);
        Assert.Null(FleetHostOperations.GetVmSafeHostPowerCapabilityBlockReason(
            "server-b",
            action,
            capabilityReceivedOnCurrentConnection: true));
    }
}
