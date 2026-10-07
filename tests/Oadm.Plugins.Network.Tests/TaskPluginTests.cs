using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Network.Tests;

public sealed class TaskPluginTests
{
    private static readonly ReachabilityOptions Fast = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(1));

    private static NetworkSettingsTaskPlugin Plugin() => new(Fast, TimeProvider.System);

    /// <summary>Runs the plugin and ends its steps like the task engine.</summary>
    private static Task RunAsync(RecordingContext ctx, Guid id, string payload) =>
        StepRun.RunAsync(ctx.Steps, () => Plugin().ExecuteAsync(ctx, new FakeDevice(id), payload, CancellationToken.None));

    private const string Compat = "Check compatibility: Done";
    private const string Read = "Read current settings: Done";
    private const string ReadIpv6 = "Read IPv6 address mode: Done";
    private const string Validate = "Validate settings: Done";

    private static string StaticPayload(Guid id, string address) => new NetworkPayload
    {
        Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
        Dns = new DnsChange(false, ["10.0.0.2"]),
        Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new(address) },
    }.ToJson();

    [Fact]
    public void Declares_a_context_menu_task_with_dialog()
    {
        var plugin = new NetworkSettingsTaskPlugin();
        Assert.Equal("oadm.network", plugin.Id);
        Assert.Equal("Network settings...", plugin.DisplayName);
        Assert.True(plugin.RequiresDialog);
        Assert.False(plugin.ShowInToolbar);
        Assert.IsAssignableFrom<ITaskPluginQuery>(plugin);
    }

    [Fact]
    public void CanRun_checks_the_cached_api_list_and_status()
    {
        var plugin = new NetworkSettingsTaskPlugin();
        Assert.True(plugin.CanRun(new FakeDevice(Guid.NewGuid())));
        Assert.True(plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = Fixture.LegacyOnly }));
        Assert.True(plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.Unknown)));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = [] }), "API list not known yet");
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = [new DeviceApi("network-settings", "2.0")] }), "different major version");
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.CredentialsRequired)));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.Unreachable)));
    }

    [Fact]
    public async Task Fresh_api_list_is_checked_before_any_write()
    {
        var id = Guid.NewGuid();
        // Cached list says yes, the device now says no (e.g. firmware downgrade).
        var vapix = new FakeNetworkVapix { ApiList = [new DeviceApi("basic-device-info", "1.3")] };
        var ctx = new RecordingContext(vapix);

        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(ctx, id, StaticPayload(id, "10.0.0.60")));

        Assert.Equal(
            ["Check compatibility: Failed", "Read current settings: Skipped", "Read IPv6 address mode: Skipped", "Validate settings: Skipped", "Set host name: Skipped", "Set DNS: Skipped",
             "Set IPv6: Skipped", "Set IPv4: Skipped", "Wait for the settings to apply: Skipped", "Check reachability: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(1, vapix.ApiListCalls);
        Assert.Empty(vapix.Sent);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_method_fails_before_the_first_write()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix { ApiList = Fixture.JsonOnly };
        var payload = new NetworkPayload
        {
            HostName = new HostNameChange(true),
            Ipv6 = new Ipv6Change(Ipv6Mode.Dhcp), // needs param.cgi
            Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new() },
        }.ToJson();

        var ctx = new RecordingContext(vapix);
        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(ctx, id, payload));

        Assert.Empty(vapix.Writes);
        Assert.Equal([Compat, Read, "Read IPv6 address mode: Skipped", "Validate settings: Failed", "Set host name: Skipped"], StepRun.Lines(ctx.Steps)[..5]);
        Assert.Equal("param.cgi is not available", StepRun.Detail(ctx.Steps, "Read IPv6 address mode"));
    }

    [Fact]
    public async Task Invalid_payload_never_touches_the_device()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);
        var ex = await Assert.ThrowsAsync<NetworkValidationException>(() => RunAsync(ctx, id, StaticPayload(id, "10.0.0.255")));

        Assert.Contains("broadcast", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ctx.Steps.Count); // validated before any step or request
        Assert.Empty(vapix.Sent);
        Assert.Equal(0, vapix.ApiListCalls);
    }

    [Fact]
    public async Task Readdressed_device_that_stops_answering_finishes_with_a_warning()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix { Answers = _ => false };
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.60"));

        var writes = vapix.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Contains("setResolverConfiguration", writes[0].Body, StringComparison.Ordinal);
        Assert.Contains("setIPv4AddressConfiguration", writes[1].Body, StringComparison.Ordinal); // address change last
        var warning = Assert.Single(ctx.Warnings);
        Assert.Contains("no longer answers at 10.0.0.48", warning, StringComparison.Ordinal);
        Assert.Contains("10.0.0.60", warning, StringComparison.Ordinal);
        Assert.Contains(ctx.LogEntries, l => l.Level == TaskLogLevel.Warning && l.Message.Contains("re-addressed from 10.0.0.48 to 10.0.0.60", StringComparison.Ordinal));
        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Warning"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(warning, StepRun.Detail(ctx.Steps, "Check reachability"));
        Assert.Equal("Keep unchanged", StepRun.Detail(ctx.Steps, "Set host name"));
        Assert.Equal("DNS 10.0.0.2", StepRun.Detail(ctx.Steps, "Set DNS"));
        Assert.Empty(ctx.Progress); // progress is derived from the steps
    }

    [Fact]
    public async Task Readdressed_device_still_answering_at_the_old_address_is_suspicious()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix { Answers = _ => true };
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.60"));

        Assert.Contains("still answers at 10.0.0.48", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.True(vapix.Pings > 1);
    }

    [Fact]
    public async Task Same_address_and_still_reachable_is_a_clean_success()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.48"));

        Assert.Empty(ctx.Warnings);
        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("network-settings 1.37", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("2 sections to change", StepRun.Detail(ctx.Steps, "Validate settings"));
        Assert.Equal("The device still answers at 10.0.0.48", StepRun.Detail(ctx.Steps, "Check reachability"));
        Assert.Equal(100, ctx.Steps.Progress);
    }

    [Fact]
    public async Task Same_address_but_unreachable_warns_about_mask_and_gateway()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix { Answers = _ => false };
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.48"));

        Assert.Contains("subnet mask and gateway", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dns_only_change_does_not_probe()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var payload = new NetworkPayload { Dns = new DnsChange(true), Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new() } }.ToJson();
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, payload);

        Assert.Single(vapix.Writes);
        Assert.Equal(0, vapix.Pings);
        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Skipped",
             "Wait for the settings to apply: Skipped", "Check reachability: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The change does not affect how OADM reaches the device.", StepRun.Detail(ctx.Steps, "Check reachability"));
    }

    [Fact]
    public async Task Device_error_names_what_was_already_applied()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix
        {
            WriteResponse = r => r.Body.Contains("setIPv4AddressConfiguration", StringComparison.Ordinal)
                ? """{"apiVersion":"1.37","context":"oadm","method":"setIPv4AddressConfiguration","error":{"code":4004,"message":"Invalid parameter(s)","details":{"subCode":107}}}"""
                : null,
        };
        var ctx = new RecordingContext(vapix);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, id, StaticPayload(id, "10.0.0.60")));

        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Failed",
             "Wait for the settings to apply: Skipped", "Check reachability: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Set IPv4"));
        Assert.Contains("(4004/107)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Already applied: DNS 10.0.0.2.", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ctx.LogEntries, l => l.Level == TaskLogLevel.Error);
        Assert.Equal(0, vapix.Pings);
    }

    [Fact]
    public async Task Legacy_param_error_is_reported()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix { ApiList = Fixture.LegacyOnly, WriteResponse = _ => "# Error: Error setting 'Network.DNSServer1' to '10.0.0.2'!" };
        var ctx = new RecordingContext(vapix);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, id, StaticPayload(id, "10.0.0.60")));

        Assert.Contains("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Single(vapix.Writes);
        Assert.Equal("Check compatibility: Done", StepRun.Lines(ctx.Steps)[0]);
        Assert.Equal("param.cgi", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("Read IPv6 address mode: Skipped", StepRun.Lines(ctx.Steps)[2]);
        Assert.Equal("Read with the current settings", StepRun.Detail(ctx.Steps, "Read IPv6 address mode"));
        Assert.Equal(["Set DNS: Failed", "Set IPv6: Skipped", "Set IPv4: Skipped"], StepRun.Lines(ctx.Steps)[5..8]);
    }

    [Fact]
    public async Task Each_ipv6_request_is_its_own_step()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var payload = new NetworkPayload { Ipv6 = new Ipv6Change(Ipv6Mode.Dhcp), Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new() } }.ToJson();
        var ctx = new RecordingContext(vapix);

        await RunAsync(ctx, id, payload);

        var lines = StepRun.Lines(ctx.Steps);
        Assert.Equal(vapix.Writes.Count, lines.Count(l => l.StartsWith("Set ", StringComparison.Ordinal) && l.EndsWith(": Done", StringComparison.Ordinal))
            + lines.Count(l => l == "Enable IPv6: Done"));
        Assert.Contains("Set IPv6: Done", lines);
        Assert.Contains("Set IPv4: Skipped", lines);
    }

    [Fact]
    public async Task Query_returns_current_settings_read_only()
    {
        var vapix = new FakeNetworkVapix();
        var json = await Plugin().QueryAsync(new RecordingContext(vapix), new FakeDevice(Guid.NewGuid()), "getNetworkInfo", null, CancellationToken.None);

        var current = CurrentNetworkSettings.FromJson(json!);
        Assert.Equal("10.0.0.48", current.Ipv4.Address);
        Assert.Equal("auto", current.Ipv6.Mode);
        Assert.Empty(vapix.Writes);
        Assert.Single(vapix.Sent); // getNetworkInfo only
        Assert.StartsWith("""{"apiVersion":"1.37","context":"oadm","method":"getNetworkInfo",""", vapix.Sent[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_on_a_legacy_device_reads_param_cgi()
    {
        var vapix = new FakeNetworkVapix { ApiList = Fixture.LegacyOnly };
        var json = await Plugin().QueryAsync(new RecordingContext(vapix), new FakeDevice(Guid.NewGuid()), "getNetworkInfo", null, CancellationToken.None);
        Assert.Equal("param.cgi", CurrentNetworkSettings.FromJson(json!).Source);
        Assert.Empty(vapix.Sent);
    }

    [Fact]
    public async Task Unknown_query_is_rejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Plugin().QueryAsync(new RecordingContext(new FakeNetworkVapix()), new FakeDevice(Guid.NewGuid()), "setEverything", null, CancellationToken.None));
    }
}
