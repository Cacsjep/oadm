using Oadm.Core.Plugins;
using Oadm.Sdk.Network;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The DHCP plugin keeps its UDP 67 firewall rule (Windows service only) in line with the enabled setting.</summary>
public sealed class FirewallRuleTests
{
    private readonly FakeDhcpNetwork _network = new();
    private readonly FakeProbe _probe = new();
    private readonly InMemoryPluginSettingsProvider _settings = new();
    private readonly RecordingFirewall _firewall = new();

    private async Task<DhcpServerPlugin> StartAsync(IFirewallRules? firewall)
    {
        var plugin = new DhcpServerPlugin(Options.Test(_network, _probe));
        await plugin.StartAsync(new TestCoreContext(_settings.GetSettings(DhcpServerPluginInfo.PluginId), null, firewall), default);
        return plugin;
    }

    private static async Task<DhcpSaveReply> SaveAsync(DhcpServerPlugin plugin, DhcpSaveRequest request) =>
        DhcpJson.Deserialize<DhcpSaveReply>(await plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(request), default));

    [Fact]
    public async Task Rule_is_opened_while_enabled_and_closed_when_disabled_or_stopped()
    {
        await using var plugin = await StartAsync(_firewall);
        var rule = plugin.Service!.Firewall.Rule;
        Assert.Equal(new FirewallRule("OADM Server (DHCP, UDP 67)", FirewallProtocol.Udp, 67), rule);
        Assert.Equal(["close " + rule.Name], _firewall.CallList);

        Assert.True((await SaveAsync(plugin, Options.Enable())).Saved);
        Assert.Equal(["close " + rule.Name, "open " + rule.Name], _firewall.CallList);

        Assert.True((await SaveAsync(plugin, Options.Enable() with { Enabled = false })).Saved);
        Assert.Equal("close " + rule.Name, _firewall.CallList[^1]);

        await SaveAsync(plugin, Options.Enable());
        await plugin.StopAsync(default);
        Assert.Equal(["close " + rule.Name, "open " + rule.Name, "close " + rule.Name, "open " + rule.Name, "close " + rule.Name], _firewall.CallList);
    }

    [Fact]
    public async Task Rule_is_opened_at_start_when_the_stored_setting_is_enabled()
    {
        await using (var first = await StartAsync(null))
        {
            await SaveAsync(first, Options.Enable());
        }

        await using var plugin = await StartAsync(_firewall);
        Assert.Equal(["open OADM Server (DHCP, UDP 67)"], _firewall.CallList);
    }

    [Fact]
    public async Task A_firewall_error_never_stops_the_dhcp_server()
    {
        _firewall.Failure = new InvalidOperationException("netsh exit code 1");
        await using var plugin = await StartAsync(_firewall);

        Assert.True((await SaveAsync(plugin, Options.Enable())).Saved);

        Assert.True(plugin.Service!.IsRunning);
        Assert.False(plugin.Service.Firewall.IsOpen);
    }
}
