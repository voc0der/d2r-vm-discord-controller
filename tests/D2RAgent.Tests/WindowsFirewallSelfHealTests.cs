using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class WindowsFirewallSelfHealTests
{
    [Fact]
    public void AgentOutboundRuleAllowsControllerRemotePortAndIp()
    {
        var ok = WindowsFirewallSelfHeal.TryBuildAgentControllerOutboundTcpRule(
            "http://192.168.10.1:8080/agent",
            @"C:\D2ROps\D2RAgent.exe",
            out var spec,
            out _);

        Assert.True(ok);
        var args = WindowsFirewallSelfHeal.BuildNetshAddRuleArguments(spec);
        Assert.Equal("D2ROps Agent outbound TCP D2RAgent 192.168.10.1 8080", spec.Name);
        Assert.Contains("dir=out", args);
        Assert.Contains("remoteport=8080", args);
        Assert.Contains("remoteip=192.168.10.1", args);
        Assert.Contains(@"program=C:\D2ROps\D2RAgent.exe", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("localport=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentOutboundRuleUsesDefaultHttpsPort()
    {
        var ok = WindowsFirewallSelfHeal.TryBuildAgentControllerOutboundTcpRule(
            "wss://controller.example/agent",
            null,
            out var spec,
            out _);

        Assert.True(ok);
        var args = WindowsFirewallSelfHeal.BuildNetshAddRuleArguments(spec);
        Assert.Equal("D2ROps Agent outbound TCP controller.example 443", spec.Name);
        Assert.Contains("remoteport=443", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("remoteip=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, arg => arg.StartsWith("program=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentOutboundRuleNameChangesWithPublishedExeName()
    {
        var originalOk = WindowsFirewallSelfHeal.TryBuildAgentControllerOutboundTcpRule(
            "http://192.168.10.1:8080/agent",
            @"C:\D2ROps\D2RAgent.exe",
            out var original,
            out _);
        var renamedOk = WindowsFirewallSelfHeal.TryBuildAgentControllerOutboundTcpRule(
            "http://192.168.10.1:8080/agent",
            @"C:\D2ROps\OpsAgent.exe",
            out var renamed,
            out _);

        Assert.True(originalOk);
        Assert.True(renamedOk);
        Assert.NotEqual(original.Name, renamed.Name);
        Assert.Equal("D2ROps Agent outbound TCP OpsAgent 192.168.10.1 8080", renamed.Name);
    }

    [Fact]
    public void AgentOutboundRuleRejectsInvalidControllerUrl()
    {
        var ok = WindowsFirewallSelfHeal.TryBuildAgentControllerOutboundTcpRule(
            "not a uri",
            null,
            out _,
            out var message);

        Assert.False(ok);
        Assert.Contains("absolute URI", message);
    }
}
