using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Network.Tests;

public sealed class TaskPluginTests
{
    private static readonly ReachabilityOptions Fast =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(1))
        {
            NewAddressTimeout = TimeSpan.FromMilliseconds(60), // never wait the real 90 s for a fake device
        };

    private static NetworkSettingsTaskPlugin Plugin(IAddressProbe? probe = null) => new(Fast, TimeProvider.System, probe ?? new FakeAddressProbe());

    /// <summary>Runs the plugin and ends its steps like the task engine.</summary>
    private static Task RunAsync(RecordingContext ctx, Guid id, string payload, IAddressProbe? probe = null) =>
        StepRun.RunAsync(ctx.Steps, () => Plugin(probe).ExecuteAsync(ctx, new FakeDevice(id), payload, CancellationToken.None));

    private const string Free = "Check address is free: Done";
    private const string FreeSkipped = "Check address is free: Skipped";
    private const string Completed = "Completed: Done";

    private const string Compat = "Check compatibility: Done";
    private const string Read = "Read current settings: Done";
    private const string ReadIpv6 = "Read IPv6 address mode: Done";
    private const string Validate = "Validate settings: Done";

    private static readonly string[] FollowSkipped =
        ["Wait for the device at the new address: Skipped", "Verify device identity: Skipped", "Update OADM device address: Skipped"];

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
        Assert.Equal("Network settings", plugin.DisplayName);
        Assert.Equal(TaskGroups.Network, ((ITaskPlugin)plugin).Group);
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
    public void NotSupportedReason_names_the_status_or_the_missing_network_api()
    {
        foreach (ITaskPlugin plugin in new ITaskPlugin[] { new NetworkSettingsTaskPlugin(), new AssignIpTaskPlugin() })
        {
            Assert.Equal("The device does not answer", plugin.NotSupportedReason(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.Unreachable)));
            Assert.Equal("OADM has not read what this device supports yet: refresh the device", plugin.NotSupportedReason(new FakeDevice(Guid.NewGuid()) { Apis = [] }));
            Assert.Equal("Needs the network settings API (this device has 12.11.77)", plugin.NotSupportedReason(new FakeDevice(Guid.NewGuid()) { Apis = [new DeviceApi("network-settings", "2.0")] }));
        }
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
            ["Check compatibility: Failed", "Read current settings: Skipped", "Read IPv6 address mode: Skipped", "Validate settings: Skipped", FreeSkipped, "Set host name: Skipped", "Set DNS: Skipped",
             "Set IPv6: Skipped", "Set IPv4: Skipped", "Wait for the settings to apply: Skipped", "Check reachability: Skipped", .. FollowSkipped],
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
        Assert.Equal([Compat, Read, "Read IPv6 address mode: Skipped", "Validate settings: Failed", FreeSkipped], StepRun.Lines(ctx.Steps)[..5]);
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
    public async Task Same_address_and_still_reachable_is_a_clean_success()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);

        var probe = new FakeAddressProbe { Answers = { ["10.0.0.48"] = new(true, true) } };
        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.48"), probe);

        Assert.Empty(ctx.Warnings);
        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, Free, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Done", .. FollowSkipped, Completed],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("10.0.0.48 is the device's own address", StepRun.Detail(ctx.Steps, "Check address is free"));
        Assert.Empty(probe.Probed); // the device's own address is never probed
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
            [Compat, Read, ReadIpv6, Validate, FreeSkipped, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Skipped",
             "Wait for the settings to apply: Skipped", "Check reachability: Skipped", .. FollowSkipped, Completed],
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
            [Compat, Read, ReadIpv6, Validate, Free, "Set host name: Skipped", "Set DNS: Done", "Set IPv6: Skipped", "Set IPv4: Failed",
             "Wait for the settings to apply: Skipped", "Check reachability: Skipped", .. FollowSkipped],
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
        Assert.Equal([Free, "Set host name: Skipped", "Set DNS: Failed", "Set IPv6: Skipped", "Set IPv4: Skipped"], StepRun.Lines(ctx.Steps)[4..9]);
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

    [Theory]
    [InlineData(true, false, "10.0.0.60 is already in use (answers ping). Nothing was changed.")]
    [InlineData(false, true, "10.0.0.60 is already in use (answers on port 80/443). Nothing was changed.")]
    [InlineData(true, true, "10.0.0.60 is already in use (answers ping). Nothing was changed.")]
    public async Task Address_in_use_fails_before_any_write(bool ping, bool tcp, string message)
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);
        var probe = new FakeAddressProbe { Answers = { ["10.0.0.60"] = new(ping, tcp) } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, id, StaticPayload(id, "10.0.0.60"), probe));

        Assert.Equal(message, ex.Message);
        Assert.Equal(["10.0.0.60"], probe.Probed);
        Assert.Empty(vapix.Writes); // not even DNS
        Assert.Equal(
            [Compat, Read, ReadIpv6, Validate, "Check address is free: Failed", "Set host name: Skipped", "Set DNS: Skipped", "Set IPv6: Skipped", "Set IPv4: Skipped",
             "Wait for the settings to apply: Skipped", "Check reachability: Skipped", .. FollowSkipped],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(message, StepRun.Detail(ctx.Steps, "Check address is free"));
    }

    [Fact]
    public async Task Free_address_is_probed_then_written()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);
        var probe = new FakeAddressProbe();

        await RunAsync(ctx, id, StaticPayload(id, "10.0.0.60"), probe);

        Assert.Equal(["10.0.0.60"], probe.Probed);
        Assert.Equal("10.0.0.60 is free", StepRun.Detail(ctx.Steps, "Check address is free"));
        Assert.Equal(2, vapix.Writes.Count);
        Assert.Equal(Completed, StepRun.Lines(ctx.Steps)[^1]);
    }

    [Fact]
    public async Task Dhcp_skips_the_address_check()
    {
        var id = Guid.NewGuid();
        var probe = new FakeAddressProbe();
        var payload = new NetworkPayload { Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp), Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new() } }.ToJson();
        var ctx = new RecordingContext(new FakeNetworkVapix());

        await RunAsync(ctx, id, payload, probe);

        Assert.Empty(probe.Probed);
        Assert.Equal("DHCP: no static address is set", StepRun.Detail(ctx.Steps, "Check address is free"));
    }

    [Fact]
    public async Task Static_ipv6_address_is_checked_too()
    {
        var id = Guid.NewGuid();
        var vapix = new FakeNetworkVapix();
        var probe = new FakeAddressProbe { Answers = { ["2001:db8::60"] = new(true, false) } };
        var payload = new NetworkPayload
        {
            Ipv6 = new Ipv6Change(Ipv6Mode.Static, null, 64),
            Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new(Ipv6Address: "2001:db8::60") },
        }.ToJson();
        var ctx = new RecordingContext(vapix);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, id, payload, probe));

        Assert.Equal("2001:db8::60 is already in use (answers ping). Nothing was changed.", ex.Message);
        Assert.Empty(vapix.Writes);
    }

    [Theory]
    [InlineData("""{"ipv4":{"mode":"static","prefixLength":24,"gateway":"10.0.0.138"},"devices":{"00000000-0000-0000-0000-000000000001":{"ipv4Address":"10.0.0.60"}}}""", "Set static IP 10.0.0.60")]
    [InlineData("""{"ipv4":{"mode":"static","prefixLength":24,"gateway":"10.0.0.138"},"devices":{"00000000-0000-0000-0000-000000000001":{"ipv4Address":"10.0.0.60"},"00000000-0000-0000-0000-000000000002":{"ipv4Address":"10.0.0.61"}}}""", "Set static IP addresses")]
    [InlineData("""{"ipv4":{"mode":"dhcp"},"devices":{}}""", "Switch to DHCP")]
    [InlineData("""{"dns":{"useDhcp":false,"servers":["10.0.0.2"]},"devices":{}}""", "Set DNS servers")]
    [InlineData("""{"dns":{"useDhcp":true},"devices":{}}""", "Use DNS from DHCP")]
    [InlineData("""{"hostName":{"useDhcp":false},"devices":{"00000000-0000-0000-0000-000000000001":{"hostName":"cam-1"}}}""", "Set host name cam-1")]
    [InlineData("""{"hostName":{"useDhcp":false},"devices":{"00000000-0000-0000-0000-000000000001":{"hostName":"cam-1"},"00000000-0000-0000-0000-000000000002":{"hostName":"cam-2"}}}""", "Set host names")]
    [InlineData("""{"hostName":{"useDhcp":true},"devices":{}}""", "Use host name from DHCP")]
    [InlineData("""{"ipv6":{"mode":"auto"},"devices":{}}""", "Change IPv6 settings")]
    [InlineData("""{"ipv4":{"mode":"dhcp"},"dns":{"useDhcp":true},"devices":{}}""", "Change network settings")]
    [InlineData("not json", "Network settings")]
    [InlineData(null, "Network settings")]
    public void Task_name_says_what_the_task_does(string? payload, string expected)
    {
        var name = ((ITaskPlugin)Plugin()).GetTaskName(payload);

        Assert.Equal(expected, name);
        Assert.True(name.Length <= TaskPluginNames.MaxTaskNameLength);
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
