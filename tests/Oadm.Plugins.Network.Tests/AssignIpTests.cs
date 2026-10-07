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

        await StepRun.RunAsync(ctx.Steps, () => new AssignIpTaskPlugin(Fast, TimeProvider.System).ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.Equal(
            ["Check compatibility: Done", "Read current settings: Done", "Validate settings: Done", "Set DNS: Done", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Skipped", "Wait for the device at the new address: Done",
             "Verify device identity: Done", "Update OADM device address: Done"],
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

        await StepRun.RunAsync(ctx.Steps, () => new AssignIpTaskPlugin(Fast, TimeProvider.System).ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.Equal(
            ["Check compatibility: Done", "Read current settings: Done", "Validate settings: Done", "Set DNS: Skipped", "Set IPv4: Done",
             "Wait for the settings to apply: Done", "Check reachability: Done", "Wait for the device at the new address: Skipped",
             "Verify device identity: Skipped", "Update OADM device address: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("DHCP: address assigned by the network, the device will be found again by the next scan", StepRun.Detail(ctx.Steps, "Update OADM device address"));
    }

    [Fact]
    public async Task Check_query_lists_managed_devices_without_probing()
    {
        var managed = new FakeDevice(Guid.NewGuid(), "10.0.0.20", Serial: "ACCC8E0000AA");
        var ctx = new QueryContext(new FakeNetworkVapix(), [managed]);

        var json = await new AssignIpTaskPlugin().QueryAsync(ctx, managed, "checkAddresses", new AddressCheckRequest(["10.0.0.20", "bad", "10.0.0.21"], Probe: false).ToJson(), CancellationToken.None);

        var response = AddressCheckResponse.Parse(json);
        var device = Assert.Single(response.Managed);
        Assert.Equal(("10.0.0.20", managed.Id, "P3265-V ACCC8E0000AA"), (device.Address, device.DeviceId, device.Device));
        Assert.Equal(["10.0.0.20", "10.0.0.21"], response.Probed.Select(p => p.Address));
        Assert.All(response.Probed, p => Assert.False(p.InUse));
        await Assert.ThrowsAsync<ArgumentException>(() => new AssignIpTaskPlugin().QueryAsync(ctx, managed, "checkAddresses",
            new AddressCheckRequest([.. Enumerable.Range(0, 300).Select(i => $"10.0.{i / 250}.{(i % 250) + 1}")]).ToJson(), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => new AssignIpTaskPlugin().QueryAsync(ctx, managed, "setEverything", null, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_marks_an_address_with_a_listening_host_in_use()
    {
        // A local listener stands in for a device's web server; TEST-NET-1 (RFC 5737) is never assigned.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await AddressCheck.AnswersAsync("127.0.0.1", [port], CancellationToken.None));
        Assert.False(await AddressCheck.AnswersAsync("192.0.2.1", [port], CancellationToken.None));
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
        Assert.Contains("10.0.0.48 (DHCP)", vm.CurrentText, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_one_validates_range_mask_router_and_dns()
    {
        var vm = Filled(2, "10.0.0.300");
        Assert.Contains(vm.Errors, e => e.Contains("must be a number 0-255", StringComparison.Ordinal));
        Assert.False(vm.CanContinue);

        vm.IpRange = "10.0.0.100-110";
        vm.DefaultRouter = "10.0.0.255";
        Assert.Contains(vm.Errors, e => e.Contains("Default router 10.0.0.255 is the broadcast address", StringComparison.Ordinal));

        vm.DefaultRouter = "10.0.0.138";
        vm.DnsPrimary = "dns.example.com";
        Assert.Contains(vm.Errors, e => e.Contains("is not a valid IP address", StringComparison.Ordinal));

        vm.DnsPrimary = "10.0.0.2";
        Assert.Empty(vm.Errors);
        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void Next_suggests_addresses_in_grid_order_and_finish_builds_the_payload()
    {
        var vm = Filled(3);
        vm.DnsPrimary = "10.0.0.2";
        vm.PrimaryCommand.Execute(null);

        Assert.True(vm.IsReviewPage);
        Assert.Equal("Finish", vm.PrimaryText);
        Assert.Equal(["10.0.0.100", "10.0.0.101", "10.0.0.102"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.Equal(["AC:CC:8E:00:00:01", "AC:CC:8E:00:00:02", "AC:CC:8E:00:00:03"], vm.Assignment.Rows.Select(r => r.MacAddress));
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Ready", r.StatusText));
        Assert.True(vm.ShowWarning);
        Assert.Contains("3 of 3 devices get a new IPv4 address", vm.Warning, StringComparison.Ordinal);
        Assert.False(vm.CanContinue); // the warning must be acknowledged

        vm.Assignment.Rows[1].NewAddress = "10.0.0.150"; // edit one row
        vm.WarningAcknowledged = true;
        Assert.True(vm.CanContinue);

        var closed = false;
        vm.CloseRequested += (_, finish) => closed = finish;
        vm.PrimaryCommand.Execute(null);

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
        vm.WarningAcknowledged = true;

        Assert.Equal("Not enough addresses: the IP range has 2 free addresses for 3 devices. Extend the range.", vm.Assignment.Error);
        Assert.Contains(vm.Errors, e => e.StartsWith("Not enough addresses", StringComparison.Ordinal));
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
        vm.WarningAcknowledged = true;
        vm.Assignment.Rows[1].NewAddress = "10.0.0.100";

        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Assigned to more than one device", r.Conflict));
        Assert.False(vm.CanContinue);
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
        vm.WarningAcknowledged = true;
        Assert.False(vm.CanContinue);
    }

    [Fact]
    public void Dhcp_finishes_on_the_first_page_after_the_warning()
    {
        var vm = new AssignIpViewModel(Devices(2)) { UseDhcp = true };
        Assert.Equal("Finish", vm.PrimaryText);
        Assert.True(vm.ShowWarning);
        Assert.Contains("DHCP server", vm.Warning, StringComparison.Ordinal);
        Assert.False(vm.CanContinue);

        vm.WarningAcknowledged = true;
        vm.PrimaryCommand.Execute(null);

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
