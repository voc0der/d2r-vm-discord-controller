using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class WindowsFirewallSelfHealTests
{
    [Fact]
    public void HostInboundRuleAllowsConfiguredLocalPort()
    {
        var spec = WindowsFirewallSelfHeal.BuildHostInboundTcpRule(8080, @"C:\D2ROps\D2RHost.exe");
        var args = WindowsFirewallSelfHeal.BuildNetshAddRuleArguments(spec);

        Assert.Equal("D2ROps Host inbound TCP 8080", spec.Name);
        Assert.Contains("dir=in", args);
        Assert.Contains("action=allow", args);
        Assert.Contains("protocol=TCP", args);
        Assert.Contains("localport=8080", args);
        Assert.Contains(@"program=C:\D2ROps\D2RHost.exe", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("remoteport=", StringComparison.OrdinalIgnoreCase));
    }

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
        Assert.Equal("D2ROps Agent outbound TCP 192.168.10.1 8080", spec.Name);
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
        Assert.Contains("remoteport=443", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("remoteip=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, arg => arg.StartsWith("program=", StringComparison.OrdinalIgnoreCase));
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
