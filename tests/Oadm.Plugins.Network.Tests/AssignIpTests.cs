using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Network.Tests;

/// <summary>"Assign IP address...": task plugin, payload, step sequence, dialog view model, address check query.</summary>
public sealed class AssignIpTests
{
    private static readonly ReachabilityOptions Fast =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(1))
        {
            NewAddressTimeout = TimeSpan.FromMilliseconds(60),
        };

    private static readonly CurrentNetworkSettings Current =
        NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));

    private static List<IDeviceInfo> Devices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{48 + i}", Serial: $"ACCC8E00000{i + 1}"))];

    [Fact]
    public void Declares_a_toolbar_and_context_menu_task_with_dialog()
    {
        var plugin = new AssignIpTaskPlugin();
        Assert.Equal("oadm.network.assign-ip", plugin.Id);
        Assert.Equal("Assign IP address", plugin.DisplayName);
        Assert.Equal(TaskGroups.Network, ((ITaskPlugin)plugin).Group);
        Assert.True(plugin.ShowInToolbar);
        Assert.True(plugin.RequiresDialog);
        Assert.True(plugin.CanRun(new FakeDevice(Guid.NewGuid())));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.Unreachable)));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = [] }));
    }

    [Fact]
    public void Payload_may_only_carry_ipv4_and_dns()
    {
        var id = Guid.NewGuid();
        var devices = new Dictionary<Guid, DeviceAssignment> { [id] = new("10.0.0.60") };
        Assert.Throws<NetworkValidationException>(() => AssignIpTaskPlugin.Validate(new NetworkPayload { Dns = new DnsChange(true), Devices = devices }));
        Assert.Throws<NetworkValidationException>(() => AssignIpTaskPlugin.Validate(new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp),
            HostName = new HostNameChange(true),
            Devices = devices,
        }));
        var ok = new NetworkPayload { Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.1"), Devices = devices };
        Assert.Same(ok, AssignIpTaskPlugin.Validate(ok));
    }

    [Fact]
    public async Task Static_assignment_runs_only_the_steps_that_apply_and_follows_the_device()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var vapix = new FakeNetworkVapix { Answers = _ => false };
        var ctx = new RecordingContext(vapix);
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix();
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
            Dns = new DnsChange(false, ["10.0.0.2"], KeepDomains: true),
            Devices = new Dictionary<Guid, DeviceAssignment> { [device.Id] = new("10.0.0.60") },
        }.ToJson();

        await StepRun.RunAsync(ctx.Steps, () => new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.Equal(
            ["Check compatibility: Done", "Read current settings: Done", "Validate settings: Done", "Check address is free: Done", "Set DNS: Done", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Skipped", "Wait for the device at the new address: Done",
             "Verify device identity: Done", "Update OADM device address: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(["10.0.0.60"], ctx.AddressUpdates);

        // DNS servers only: the device's own domain name and search domains are written back unchanged.
        var dns = vapix.Writes[0].Body;
        Assert.Contains("setResolverConfiguration", dns, StringComparison.Ordinal);
        Assert.Contains("\"staticNameServers\":[\"10.0.0.2\"]", dns, StringComparison.Ordinal);
        Assert.Equal(2, vapix.Writes.Count);
        Assert.DoesNotContain(vapix.Sent, r => r.Path.EndsWith("param.cgi", StringComparison.Ordinal)); // no IPv6 mode read
    }

    [Fact]
    public async Task Dhcp_assignment_skips_following()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var vapix = new FakeNetworkVapix
        {
            NetworkInfoJson = Fixture.Read(Fixture.GetNetworkInfo).Replace("\"configurationMode\": \"dhcp\"", "\"configurationMode\": \"static\"", StringComparison.Ordinal),
        };
        var ctx = new RecordingContext(vapix);
        var payload = new NetworkPayload { Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp), Devices = new Dictionary<Guid, DeviceAssignment> { [device.Id] = new() } }.ToJson();

        await StepRun.RunAsync(ctx.Steps, () => new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.Equal(
            ["Check compatibility: Done", "Read current settings: Done", "Validate settings: Done", "Check address is free: Skipped", "Set DNS: Skipped", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Done", "Wait for the device at the new address: Skipped",
             "Verify device identity: Skipped", "Update OADM device address: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("OADM finds the device again at its new address.", StepRun.Detail(ctx.Steps, "Update OADM device address"));
    }

    [Fact]
    public async Task Address_in_use_is_refused_before_any_write()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var vapix = new FakeNetworkVapix();
        var ctx = new RecordingContext(vapix);
        var probe = new FakeAddressProbe { Answers = { ["10.0.0.60"] = new(true, false) } };
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
            Dns = new DnsChange(false, ["10.0.0.2"], KeepDomains: true),
            Devices = new Dictionary<Guid, DeviceAssignment> { [device.Id] = new("10.0.0.60") },
        }.ToJson();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StepRun.RunAsync(ctx.Steps, () => new AssignIpTaskPlugin(Fast, TimeProvider.System, probe).ExecuteAsync(ctx, device, payload, CancellationToken.None)));

        Assert.Equal("10.0.0.60 is already in use (answers ping). Nothing was changed.", ex.Message);
        Assert.Empty(vapix.Writes);
        Assert.Equal("Check address is free: Failed", StepRun.Lines(ctx.Steps)[3]);
        Assert.DoesNotContain("Completed: Done", StepRun.Lines(ctx.Steps));
    }

    [Theory]
    [InlineData("""{"ipv4":{"mode":"static","prefixLength":24,"gateway":"10.0.0.138"},"devices":{"00000000-0000-0000-0000-000000000001":{"ipv4Address":"10.0.0.60"}}}""", "Assign IP 10.0.0.60")]
    [InlineData("""{"ipv4":{"mode":"static","prefixLength":24,"gateway":"10.0.0.138"},"devices":{"00000000-0000-0000-0000-000000000001":{"ipv4Address":"10.0.0.60"},"00000000-0000-0000-0000-000000000002":{"ipv4Address":"10.0.0.61"}}}""", "Assign IP addresses")]
    [InlineData("""{"ipv4":{"mode":"dhcp"},"devices":{}}""", "Assign IP via DHCP")]
    [InlineData("""{"dns":{"useDhcp":true},"devices":{}}""", "Assign IP address")]
    [InlineData("{", "Assign IP address")]
    [InlineData(null, "Assign IP address")]
    public void Task_name_says_what_the_task_does(string? payload, string expected) =>
        Assert.Equal(expected, ((ITaskPlugin)new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe())).GetTaskName(payload));

    [Fact]
    public async Task Check_query_lists_managed_devices_without_probing()
    {
        var managed = new FakeDevice(Guid.NewGuid(), "10.0.0.20", Serial: "ACCC8E0000AA");
        var ctx = new QueryContext(new FakeNetworkVapix(), [managed]);

        var json = await new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).QueryAsync(ctx, managed, "checkAddresses", new AddressCheckRequest(["10.0.0.20", "bad", "10.0.0.21"], Probe: false).ToJson(), CancellationToken.None);

        var response = AddressCheckResponse.Parse(json);
        var device = Assert.Single(response.Managed);
        Assert.Equal(("10.0.0.20", managed.Id, "P3265-V ACCC8E0000AA"), (device.Address, device.DeviceId, device.Device));
        Assert.Equal(["10.0.0.20", "10.0.0.21"], response.Probed.Select(p => p.Address));
        Assert.All(response.Probed, p => Assert.False(p.InUse));
        await Assert.ThrowsAsync<ArgumentException>(() => new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).QueryAsync(ctx, managed, "checkAddresses",
            new AddressCheckRequest([.. Enumerable.Range(0, 300).Select(i => $"10.0.{i / 250}.{(i % 250) + 1}")]).ToJson(), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).QueryAsync(ctx, managed, "setEverything", null, CancellationToken.None));
    }

    [Fact]
    public async Task Check_query_probes_ipv4_and_ipv6_and_reports_ping_answers()
    {
        var managed = new FakeDevice(Guid.NewGuid(), "10.0.0.20");
        var ctx = new QueryContext(new FakeNetworkVapix(), [managed]);
        var probe = new FakeAddressProbe
        {
            Answers =
            {
                ["10.0.0.30"] = new(true, false),   // ping only
                ["10.0.0.31"] = new(false, true),   // TCP 80/443 only
                ["2001:db8::30"] = new(true, true),
            },
        };

        var json = await new AssignIpTaskPlugin(Fast, TimeProvider.System, probe).QueryAsync(ctx, managed, "checkAddresses",
            new AddressCheckRequest(["10.0.0.30", "10.0.0.31", "10.0.0.32", "2001:db8::30", "fe80::1%3"]).ToJson(), CancellationToken.None);

        var probed = AddressCheckResponse.Parse(json).Probed.ToDictionary(p => p.Address);
        Assert.Equal(["10.0.0.30", "10.0.0.31", "10.0.0.32", "2001:db8::30"], probed.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("In use (answers ping)", probed["10.0.0.30"].InUseText);
        Assert.True(probed["10.0.0.30"].InUse);
        Assert.Equal("In use (answers on port 80/443)", probed["10.0.0.31"].InUseText);
        Assert.True(probed["10.0.0.31"].InUse);
        Assert.False(probed["10.0.0.32"].InUse); // nothing answers
        Assert.True(probed["2001:db8::30"].AnswersPing);
    }

    [Fact]
    public async Task Probe_marks_an_address_with_a_listening_host_in_use()
    {
        // A local listener stands in for a device's web server; TEST-NET-1 (RFC 5737) is never assigned. No ping here.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new NetworkAddressProbe { Ports = [port], PingAttempts = 0 };
        Assert.Equal(new AddressProbeResult(false, true), await probe.ProbeAsync("127.0.0.1", CancellationToken.None));
        Assert.False((await probe.ProbeAsync("192.0.2.1", CancellationToken.None)).InUse);
    }

    [Fact]
    public void Own_address_is_recognized_in_any_notation()
    {
        Assert.True(AddressCheck.IsOwnAddress("10.0.0.48", ["10.0.0.48"]));
        Assert.True(AddressCheck.IsOwnAddress("2001:db8::10", [null, "2001:DB8:0::10/64"]));
        Assert.False(AddressCheck.IsOwnAddress("10.0.0.49", ["10.0.0.48", null]));
    }

    // ---- dialog view model ----

    private static AssignIpViewModel Filled(int count, string range = "10.0.0.100-110")
    {
        var vm = new AssignIpViewModel(Devices(count));
        vm.ApplyCurrent(Current);
        vm.IpRange = range;
        return vm;
    }

    [Fact]
    public void Starts_with_the_range_option_and_adm_wording()
    {
        var vm = new AssignIpViewModel(Devices(3));
        Assert.False(vm.UseDhcp);
        Assert.True(vm.IsSettingsPage);
        Assert.Equal("Assign IP address to 3 devices", vm.Title);
        Assert.Equal("Obtain IP addresses automatically (DHCP)", vm.DhcpOptionText);
        Assert.Equal("Assign the following IP address range", vm.RangeOptionText);
        Assert.Equal("IP range", vm.RangeLabel);
        Assert.Equal("Next", vm.PrimaryText);
        Assert.False(vm.CanContinue);

        var single = new AssignIpViewModel(Devices(1));
        Assert.Equal("Assign the following IP address", single.RangeOptionText);
        Assert.Equal("IP address", single.RangeLabel);
    }

    [Fact]
    public void Prefill_takes_mask_and_router_from_the_first_device()
    {
        var vm = new AssignIpViewModel(Devices(2));
        vm.ApplyCurrent(Current);
        Assert.Equal("255.255.255.0", vm.SubnetMask);
        Assert.Equal("10.0.0.138", vm.DefaultRouter);
        Assert.Equal(string.Empty, vm.PrefillStatus); // no info line: the values are in the fields
    }

    [Fact]
    public void Page_one_shows_each_error_below_its_field()
    {
        var vm = Filled(2, "10.0.0.300");
        Assert.Contains("must be a number 0-255", vm.ErrorOf(nameof(vm.IpRange)), StringComparison.Ordinal);
        Assert.Single(vm.GetErrors(nameof(vm.IpRange)).Cast<string>());
        Assert.False(vm.CanContinue);
        Assert.Equal(vm.ErrorOf(nameof(vm.IpRange)), vm.BlockedReason);

        vm.IpRange = "10.0.0.100-110";
        Assert.Null(vm.ErrorOf(nameof(vm.IpRange)));
        vm.DefaultRouter = "10.0.0.255";
        Assert.Equal("Default router 10.0.0.255 is the broadcast address of its subnet.", vm.ErrorOf(nameof(vm.DefaultRouter)));
        Assert.Null(vm.ErrorOf(nameof(vm.SubnetMask)));

        vm.SubnetMask = "255.0.255.0";
        Assert.NotNull(vm.ErrorOf(nameof(vm.SubnetMask)));
        vm.SubnetMask = "255.255.255.0";

        vm.DefaultRouter = "10.0.0.138";
        vm.DnsPrimary = "dns.example.com";
        Assert.Contains("is not a valid IP address", vm.ErrorOf(nameof(vm.DnsPrimary)), StringComparison.Ordinal);
        Assert.Null(vm.ErrorOf(nameof(vm.DnsSecondary)));

        vm.DnsPrimary = "10.0.0.2";
        Assert.False(vm.HasErrors);
        Assert.True(vm.CanContinue);
        Assert.Null(vm.BlockedReason);
    }

    [Fact]
    public async Task Next_suggests_addresses_in_grid_order_and_finish_confirms_and_builds_the_payload()
    {
        var vm = Filled(3);
        vm.DnsPrimary = "10.0.0.2";
        vm.PrimaryCommand.Execute(null);

        Assert.True(vm.IsReviewPage);
        Assert.Equal("Finish", vm.PrimaryText);
        Assert.Equal(["10.0.0.100", "10.0.0.101", "10.0.0.102"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.Equal(["AC:CC:8E:00:00:01", "AC:CC:8E:00:00:02", "AC:CC:8E:00:00:03"], vm.Assignment.Rows.Select(r => r.MacAddress));
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Ready", r.StatusText));
        Assert.True(vm.HasWarning);
        Assert.Contains("3 of 3 devices get a new IP address", vm.Warning, StringComparison.Ordinal);
        Assert.True(vm.CanContinue); // no inline acknowledgement: Finish asks

        vm.Assignment.Rows[1].NewAddress = "10.0.0.150"; // edit one row
        Assert.True(vm.CanContinue);

        // The confirmation is declined first: the dialog stays open.
        var asked = new List<(string Title, string Message, string Confirm)>();
        var answer = false;
        vm.Confirm = (title, message, confirm) =>
        {
            asked.Add((title, message, confirm));
            return Task.FromResult(answer);
        };
        var closed = false;
        vm.CloseRequested += (_, finish) => closed = finish;
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Null(vm.ResultJson);
        var question = Assert.Single(asked);
        Assert.Equal(("The devices get new IP addresses", vm.Warning, "Finish"), question);

        answer = true;
        await vm.PrimaryCommand.ExecuteAsync(null);

        Assert.True(closed);
        var payload = NetworkPayload.Parse(vm.ResultJson);
        Assert.Equal(new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"), payload.Ipv4);
        Assert.Equal(["10.0.0.100", "10.0.0.150", "10.0.0.102"], vm.Assignment.Rows.Select(r => payload.Devices[r.Device.Id].Ipv4Address));
        Assert.True(payload.Dns!.KeepDomains);
        Assert.Equal(["10.0.0.2"], payload.Dns.Servers);
        Assert.Null(payload.Ipv6);
        Assert.Null(payload.HostName);
        AssignIpTaskPlugin.Validate(payload); // what the server accepts
    }

    [Fact]
    public void Too_few_addresses_blocks_finish()
    {
        var vm = Filled(3, "10.0.0.253-254");
        vm.PrimaryCommand.Execute(null);

        Assert.Equal("Not enough addresses: the IP range has 2 free addresses for 3 devices. Extend the range.", vm.Assignment.Error);
        Assert.Equal(vm.Assignment.Error, vm.BlockedReason); // shown below the table, not repeated
        Assert.False(vm.HasErrors);
        Assert.Equal(string.Empty, vm.Assignment.Rows[2].NewAddress);
        Assert.Equal("No address", vm.Assignment.Rows[2].Conflict);
        Assert.False(vm.CanContinue);

        vm.Assignment.Rows[2].NewAddress = "10.0.0.20"; // the user fills the gap
        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void Edited_duplicate_is_highlighted_and_blocks_finish()
    {
        var vm = Filled(2);
        vm.PrimaryCommand.Execute(null);
        vm.Assignment.Rows[1].NewAddress = "10.0.0.100";

        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Assigned to more than one device", r.Conflict));
        Assert.False(vm.CanContinue);
        Assert.False(vm.HasErrors); // only in the Status column
        Assert.Equal("Resolve the problems shown in the table, or edit the new IP addresses.", vm.BlockedReason);
    }

    [Fact]
    public async Task Server_check_skips_addresses_in_use_and_of_other_managed_devices()
    {
        var devices = Devices(3);
        var other = Guid.NewGuid();
        var probes = new List<string>();
        var ctx = new FakeDialogContext((_, method) => Task.FromResult<string?>(method == NetworkSettingsTaskPlugin.QueryGetNetworkInfo ? Current.ToJson() : null))
        {
            Answer = (method, payload) =>
            {
                var request = AddressCheckRequest.Parse(payload);
                probes.AddRange(request.Addresses);
                return new AddressCheckResponse(
                    [new("10.0.0.101", other, "P3265-V ACCC8E0000AA"), .. devices.Select(d => new AddressStatus(d.Address, d.Id, d.Serial))],
                    [.. request.Addresses.Select(a => new AddressStatus(a, InUse: a == "10.0.0.103"))]).ToJson();
            },
        };
        var vm = new AssignIpViewModel(devices);
        await vm.LoadCurrentAsync(ctx, CancellationToken.None);
        vm.IpRange = "10.0.0.100-110";
        vm.GoToReview();
        for (var i = 0; i < 50 && vm.Assignment.IsChecking; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(["10.0.0.100", "10.0.0.102", "10.0.0.104"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.All(vm.Assignment.Rows, r => Assert.Null(r.Conflict));
        Assert.Contains("10.0.0.103", probes);
        Assert.Contains("1 address is in use", vm.Assignment.CheckStatus, StringComparison.Ordinal);

        vm.Assignment.Rows[0].NewAddress = "10.0.0.101"; // the user picks a managed device's address
        Assert.Equal("Used by P3265-V ACCC8E0000AA", vm.Assignment.Rows[0].Conflict);
        Assert.False(vm.CanContinue);
    }

    [Fact]
    public async Task Dhcp_finishes_on_the_first_page_after_the_confirmation()
    {
        var vm = new AssignIpViewModel(Devices(2)) { UseDhcp = true };
        Assert.Equal("Finish", vm.PrimaryText);
        Assert.Contains("DHCP server", vm.Warning, StringComparison.Ordinal);
        Assert.True(vm.CanContinue);
        string? title = null;
        vm.Confirm = (t, _, _) =>
        {
            title = t;
            return Task.FromResult(true);
        };

        await vm.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("The devices get their addresses from DHCP", title);
        var payload = NetworkPayload.Parse(vm.ResultJson);
        Assert.Equal(Ipv4Mode.Dhcp, payload.Ipv4!.Mode);
        Assert.Null(payload.Dns);
        Assert.Equal(2, payload.Devices.Count);
    }

    [Fact]
    public void Back_returns_to_the_settings_and_cancel_returns_nothing()
    {
        var vm = Filled(2);
        vm.PrimaryCommand.Execute(null);
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsSettingsPage);
        Assert.Equal("Next", vm.PrimaryText);

        bool? closed = null;
        vm.CloseRequested += (_, finish) => closed = finish;
        vm.CancelCommand.Execute(null);
        Assert.False(closed);
        Assert.Null(vm.ResultJson);
    }

    private sealed class QueryContext(IVapixClient vapix, IReadOnlyList<IDeviceInfo> devices) : ITaskQueryContext
    {
        public IVapixClient Vapix { get; } = vapix;

        public ILogger Logger { get; } = NullLogger.Instance;

        public IDeviceRepository? Devices { get; } = new Repository(devices);
    }

    private sealed class Repository(IReadOnlyList<IDeviceInfo> devices) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult(devices);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(devices.FirstOrDefault(d => d.Id == id));
    }
}
