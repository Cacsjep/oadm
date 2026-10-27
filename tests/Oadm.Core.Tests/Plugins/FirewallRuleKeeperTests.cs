using Oadm.Sdk.Network;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Plugins;

/// <summary>SDK helper that keeps a plugin's firewall rule in line with its enabled state.</summary>
public sealed class FirewallRuleKeeperTests
{
    private static readonly FirewallRule Rule = FirewallRule.ForService("NTP", FirewallProtocol.Udp, 123);

    [Fact]
    public void RuleNamesSayServiceProtocolAndPort()
    {
        Assert.Equal(new FirewallRule("OADM Server (NTP, UDP 123)", FirewallProtocol.Udp, 123), Rule);
        Assert.Equal("OADM Server (gRPC, TCP 5080)", FirewallRule.ForService("gRPC", FirewallProtocol.Tcp, 5080).Name);
    }

    [Fact]
    public void ARangeRuleNamesItsRangeAndOnePortStaysOnePort()
    {
        var range = FirewallRule.ForServiceRange("Load test", FirewallProtocol.Udp, 50000, 50799);

        Assert.Equal("OADM Server (Load test, UDP 50000-50799)", range.Name);
        Assert.Equal((50000, 50799), (range.Port, range.LastPort));
        Assert.Equal("50000-50799", range.PortText);
        Assert.Equal(FirewallRule.ForService("NTP", FirewallProtocol.Udp, 123), FirewallRule.ForServiceRange("NTP", FirewallProtocol.Udp, 123, 123));
        Assert.Equal("123", Rule.PortText);
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRule.ForServiceRange("x", FirewallProtocol.Udp, 50001, 50000));
    }

    [Fact]
    public async Task FirstSyncAlwaysAppliesLaterOnlyChanges()
    {
        var firewall = new RecordingFirewall();
        var keeper = new FirewallRuleKeeper(Rule, firewall);

        await keeper.SyncAsync(false, CancellationToken.None);
        await keeper.SyncAsync(false, CancellationToken.None);
        await keeper.SyncAsync(true, CancellationToken.None);
        await keeper.SyncAsync(true, CancellationToken.None);
        Assert.True(keeper.IsOpen);
        await keeper.SyncAsync(false, CancellationToken.None);

        Assert.Equal(["close " + Rule.Name, "open " + Rule.Name, "close " + Rule.Name], firewall.CallList);
        Assert.False(keeper.IsOpen);
    }

    [Fact]
    public async Task FailuresAreSwallowedAndRetried()
    {
        var firewall = new RecordingFirewall { Failure = new InvalidOperationException("netsh failed") };
        var keeper = new FirewallRuleKeeper(Rule, firewall);

        await keeper.SyncAsync(true, CancellationToken.None);
        Assert.False(keeper.IsOpen);

        firewall.Failure = null;
        await keeper.SyncAsync(true, CancellationToken.None);
        Assert.True(keeper.IsOpen);
        Assert.Equal(["open " + Rule.Name, "open " + Rule.Name], firewall.CallList);
    }

    [Fact]
    public async Task WithoutHostFirewallNothingHappens()
    {
        var keeper = new FirewallRuleKeeper(Rule, firewall: null);

        await keeper.SyncAsync(true, CancellationToken.None);

        Assert.False(keeper.IsOpen);
    }
}
