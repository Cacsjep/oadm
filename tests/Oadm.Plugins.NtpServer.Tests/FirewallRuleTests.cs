using Oadm.Core.Plugins;
using Oadm.Sdk.Network;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>The NTP plugin keeps its UDP firewall rule (Windows service only) in line with the enabled setting.</summary>
public sealed class FirewallRuleTests
{
    private readonly InMemoryPluginSettingsProvider _settings = new();
    private readonly RecordingFirewall _firewall = new();

    private async Task<NtpServerPlugin> StartAsync(IFirewallRules? firewall)
    {
        var plugin = new NtpServerPlugin(Options.Test());
        await plugin.StartAsync(new TestCoreContext(_settings.GetSettings(NtpServerPluginInfo.PluginId), null, firewall), CancellationToken.None);
        return plugin;
    }

    private static async Task<SaveReply> SaveAsync(NtpServerPlugin plugin, bool enabled) =>
        NtpJson.Deserialize<SaveReply>(await plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(enabled, "lo-id", null)), CancellationToken.None));

    [Fact]
    public async Task Rule_is_opened_while_enabled_and_closed_when_disabled_or_stopped()
    {
        await using var plugin = await StartAsync(_firewall);
        var rule = plugin.Service!.Firewall.Rule;
        Assert.Equal(FirewallProtocol.Udp, rule.Protocol);
        Assert.StartsWith("OADM Server (NTP, UDP ", rule.Name, StringComparison.Ordinal);

        // Disabled at start: a rule left by a crashed server is removed.
        Assert.Equal(["close " + rule.Name], _firewall.CallList);

        await SaveAsync(plugin, enabled: true);
        await SaveAsync(plugin, enabled: true); // unchanged: no second netsh call
        Assert.Equal(["close " + rule.Name, "open " + rule.Name], _firewall.CallList);

        await SaveAsync(plugin, enabled: false);
        await SaveAsync(plugin, enabled: true);
        await plugin.StopAsync(CancellationToken.None);
        Assert.Equal(["close " + rule.Name, "open " + rule.Name, "close " + rule.Name, "open " + rule.Name, "close " + rule.Name], _firewall.CallList);
    }

    [Fact]
    public async Task Rule_is_opened_at_start_when_the_stored_setting_is_enabled()
    {
        await using (var first = await StartAsync(null))
        {
            await SaveAsync(first, enabled: true);
        }

        await using var plugin = await StartAsync(_firewall);
        Assert.Equal(["open " + plugin.Service!.Firewall.Rule.Name], _firewall.CallList);
        Assert.True(plugin.Service.Firewall.IsOpen);
    }

    [Fact]
    public async Task A_firewall_error_never_stops_the_ntp_server()
    {
        _firewall.Failure = new InvalidOperationException("netsh exit code 1");
        await using var plugin = await StartAsync(_firewall);

        var reply = await SaveAsync(plugin, enabled: true);

        Assert.True(reply.Saved);
        Assert.Equal(ServiceStatus.Ok, reply.State.Status.Kind);
        Assert.False(plugin.Service!.Firewall.IsOpen);

        // Tried again on the next change.
        _firewall.Failure = null;
        await SaveAsync(plugin, enabled: false);
        Assert.Equal("close " + plugin.Service.Firewall.Rule.Name, _firewall.CallList[^1]);
    }
}
