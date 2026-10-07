using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network.Tests;

internal sealed class FakeDialogContext(Func<Guid, string, Task<string?>> query) : ITaskDialogContext
{
    public List<(Guid Device, string Method)> Queries { get; } = [];

    /// <summary>Answers "checkAddresses" from its payload (method, payload JSON) when set.</summary>
    public Func<string, string?, string?>? Answer { get; init; }

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        Queries.Add((deviceId, method));
        return Answer is not null && method == AddressCheck.QueryMethod ? Task.FromResult(Answer(method, payloadJson)) : query(deviceId, method);
    }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class ViewModelTests
{
    private static readonly CurrentNetworkSettings Current =
        NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));

    private static List<IDeviceInfo> Devices(int count) =>
        Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{48 + i}", Serial: $"ACCC8E00000{i + 1}")).ToList();

    /// <summary>Records every confirmation and answers with <paramref name="answer"/>.</summary>
    private static List<(string Title, string Message, string Confirm)> ConfirmWith(NetworkSettingsViewModel vm, bool answer)
    {
        var asked = new List<(string, string, string)>();
        vm.Confirm = (title, message, confirm) =>
        {
            asked.Add((title, message, confirm));
            return Task.FromResult(answer);
        };
        return asked;
    }

    [Fact]
    public void Starts_with_every_section_unchanged_and_apply_disabled()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        Assert.Equal(Ipv4Choice.Keep, vm.SelectedIpv4.Value);
        Assert.Equal(Ipv6Choice.Keep, vm.SelectedIpv6.Value);
        Assert.Equal(SourceChoice.Keep, vm.SelectedDns.Value);
        Assert.Equal(SourceChoice.Keep, vm.SelectedHostName.Value);
        Assert.False(vm.CanApply);
        Assert.False(vm.HasErrors);
        Assert.False(vm.HasWarning);
        Assert.Equal("Choose at least one setting to change.", vm.ApplyBlockedReason);
    }

    [Fact]
    public void Static_ipv6_is_offered_for_one_and_for_several_devices()
    {
        Assert.Contains(new NetworkSettingsViewModel(Devices(1)).Ipv6Choices, c => c.Value == Ipv6Choice.Static);
        Assert.Contains(new NetworkSettingsViewModel(Devices(3)).Ipv6Choices, c => c.Value == Ipv6Choice.Static);
    }

    [Fact]
    public async Task Only_touched_sections_end_up_in_the_payload()
    {
        var devices = Devices(1);
        var vm = new NetworkSettingsViewModel(devices);
        var asked = ConfirmWith(vm, answer: true);
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Dhcp);

        Assert.True(vm.CanApply);
        bool? closed = null;
        vm.CloseRequested += (_, apply) => closed = apply;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Empty(asked); // DNS cannot cut OADM off: no confirmation
        var payload = NetworkPayload.Parse(vm.ResultJson);
        Assert.Null(payload.Ipv4);
        Assert.Null(payload.Ipv6);
        Assert.Null(payload.HostName);
        Assert.True(payload.Dns!.UseDhcp);
        Assert.True(payload.Devices.ContainsKey(devices[0].Id));
    }

    [Fact]
    public void Prefills_from_the_first_device_without_changing_anything()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        vm.ApplyCurrent(Current);

        Assert.Equal("255.255.255.0", vm.Ipv4Mask);
        Assert.Equal("10.0.0.138", vm.Ipv4Gateway);
        Assert.Equal("10.0.0.48", vm.Ipv4Address);
        Assert.Equal("1.1.1.1", vm.DnsPrimary);
        Assert.Equal("axis-accc8e000001", vm.HostNameText);
        Assert.Equal(string.Empty, vm.PrefillStatus); // no "Current: ..." info lines
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Multi_device_static_sets_addresses_only_in_the_table()
    {
        var devices = Devices(3);
        var vm = new NetworkSettingsViewModel(devices);
        vm.ApplyCurrent(Current);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);

        Assert.False(vm.ShowIpv4Address); // no IP range field for several devices
        Assert.True(vm.ShowPreview);
        // Suggested from the first device's address and subnet: it keeps its address, the others follow.
        Assert.Equal(["10.0.0.48", "10.0.0.49", "10.0.0.50"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Keeps its address", r.StatusText));

        vm.Assignment.Rows[1].NewAddress = "10.0.0.101";
        vm.Assignment.Rows[2].NewAddress = "10.0.0.102";
        Assert.False(vm.HasErrors);
        Assert.True(vm.HasWarning);
        Assert.Contains("2 of 3 devices get a new IPv4 address", vm.ReachabilityWarning, StringComparison.Ordinal);
        Assert.True(vm.CanApply); // no inline acknowledgement any more

        // Declined confirmation: the dialog stays open.
        var asked = ConfirmWith(vm, answer: false);
        bool? closed = null;
        vm.CloseRequested += (_, apply) => closed = apply;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.Equal((NetworkSettingsViewModel.ConfirmTitle, vm.ReachabilityWarning, "Apply"), Assert.Single(asked));

        ConfirmWith(vm, answer: true);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(closed);
        var payload = NetworkPayload.Parse(vm.ResultJson);
        Assert.Equal(24, payload.Ipv4!.PrefixLength);
        Assert.Equal("10.0.0.101", payload.Devices[devices[1].Id].Ipv4Address);
    }

    [Fact]
    public void Without_a_known_address_every_row_is_typed_by_hand()
    {
        var vm = new NetworkSettingsViewModel(Devices(2));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.1";

        Assert.All(vm.Assignment.Rows, r => Assert.True(r.IsEditable));
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("No address", r.Conflict));
        Assert.False(vm.HasErrors); // row problems stay in the table
        Assert.False(vm.CanApply);
        Assert.Equal("Resolve the problems shown in the Devices table.", vm.ApplyBlockedReason);

        vm.Assignment.Rows[0].NewAddress = "10.0.0.60";
        vm.Assignment.Rows[1].NewAddress = "10.0.0.61";
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Range_that_does_not_fit_is_shown_below_the_table()
    {
        var vm = new NetworkSettingsViewModel(Devices(3));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.253"; // first device's address (prefill)
        vm.Ipv4Mask = "255.255.255.0";
        vm.Ipv4Gateway = "10.0.0.1";

        Assert.False(vm.HasErrors);
        Assert.StartsWith("Not enough addresses: the IP range has 2 free addresses for 3 devices", vm.Assignment.Error, StringComparison.Ordinal);
        Assert.Equal(vm.Assignment.Error, vm.ApplyBlockedReason);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Gateway_in_the_range_is_skipped_and_an_edit_to_it_is_caught()
    {
        var vm = new NetworkSettingsViewModel(Devices(3));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.100";
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.101";
        Assert.Equal(["10.0.0.100", "10.0.0.102", "10.0.0.103"], vm.Assignment.Rows.Select(r => r.NewAddress));

        vm.Assignment.Rows[2].NewAddress = "10.0.0.101";
        Assert.Equal("Same as the default router", vm.Assignment.Rows[2].Conflict);
        Assert.False(vm.HasErrors); // not repeated below the cards
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Edits_survive_mask_and_gateway_changes()
    {
        var vm = new NetworkSettingsViewModel(Devices(3));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.Ipv4Address = "10.0.0.10";
        Assert.Equal(["10.0.0.10", "10.0.0.11", "10.0.0.12"], vm.Assignment.Rows.Select(r => r.NewAddress));

        vm.Assignment.Rows[0].NewAddress = "10.0.0.30";
        vm.Ipv4Mask = "255.255.255.0"; // same network, the edit stays
        Assert.Equal(["10.0.0.30", "10.0.0.11", "10.0.0.12"], vm.Assignment.Rows.Select(r => r.NewAddress));
        vm.Ipv4Gateway = "10.0.0.2"; // suggested again around the edit
        Assert.Equal(["10.0.0.30", "10.0.0.10", "10.0.0.11"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.True(vm.CanApply);
        Assert.Equal("10.0.0.30", vm.Payload!.Devices[vm.Assignment.Rows[0].Device.Id].Ipv4Address);
    }

    [Fact]
    public void Field_errors_appear_below_their_field_once()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        var changed = new List<string?>();
        vm.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.60";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.Ipv4Mask = "255.0.255.0";

        Assert.Contains("subnet mask", vm.ErrorOf(nameof(vm.Ipv4Mask)), StringComparison.Ordinal);
        Assert.Single(vm.GetErrors(nameof(vm.Ipv4Mask)).Cast<string>());
        Assert.Null(vm.ErrorOf(nameof(vm.Ipv4Gateway)));
        Assert.Null(vm.ErrorOf(nameof(vm.Ipv4Address)));
        Assert.Contains(nameof(vm.Ipv4Mask), changed);
        Assert.Equal(vm.ErrorOf(nameof(vm.Ipv4Mask)), vm.ApplyBlockedReason);

        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.1.1";
        Assert.Null(vm.ErrorOf(nameof(vm.Ipv4Mask)));
        Assert.Equal("Outside the subnet of the default router.", vm.ErrorOf(nameof(vm.Ipv4Address)));

        vm.Ipv4Gateway = "10.0.0.255";
        Assert.Equal("Gateway 10.0.0.255 is the broadcast address of its subnet.", vm.ErrorOf(nameof(vm.Ipv4Gateway)));

        vm.Ipv4Gateway = "10.0.0.1";
        Assert.False(vm.HasErrors);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Dns_and_host_name_errors_go_to_their_fields()
    {
        var vm = new NetworkSettingsViewModel(Devices(2));
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Static);
        vm.DnsPrimary = "dns.example.com";
        vm.DnsDomain = "-bad-";
        vm.DnsSearch = "ok.example.com, _bad";
        Assert.Contains("is not a valid IP address", vm.ErrorOf(nameof(vm.DnsPrimary)), StringComparison.Ordinal);
        Assert.Contains("not a valid domain name", vm.ErrorOf(nameof(vm.DnsDomain)), StringComparison.Ordinal);
        Assert.Contains("Search domain", vm.ErrorOf(nameof(vm.DnsSearch)), StringComparison.Ordinal);

        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam";
        Assert.Contains("assigned to 2 devices", vm.ErrorOf(nameof(vm.HostNameText)), StringComparison.Ordinal);
    }

    [Fact]
    public void Host_name_template_with_tokens_is_unique()
    {
        var vm = new NetworkSettingsViewModel(Devices(2));
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam-{n}";
        Assert.False(vm.HasErrors);
        Assert.Equal(["cam-1", "cam-2"], vm.Assignment.Rows.Select(r => r.NewHostName));
        Assert.Equal(["Unchanged", "Unchanged"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.False(vm.HasWarning);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Static_ipv6_for_several_devices_is_set_per_row()
    {
        var devices = Devices(2);
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);

        Assert.False(vm.ShowIpv6Address); // no shared address field
        Assert.True(vm.ShowPreview);
        Assert.True(vm.Assignment.ShowIpv6);
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("IPv6: No IPv6 address", r.StatusText));
        Assert.False(vm.CanApply);

        vm.Assignment.Rows[0].NewIpv6Address = "2001:db8::60";
        vm.Assignment.Rows[1].NewIpv6Address = "2001:db8::60";
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Assigned to more than one device", r.Ipv6Conflict));

        vm.Assignment.Rows[1].NewIpv6Address = "ff02::1";
        Assert.Equal("Multicast address", vm.Assignment.Rows[1].Ipv6Conflict);

        vm.Assignment.Rows[1].NewIpv6Address = "2001:db8::61";
        vm.Ipv6Prefix = "200";
        Assert.Contains("prefix length", vm.ErrorOf(nameof(vm.Ipv6Prefix)), StringComparison.Ordinal);
        vm.Ipv6Prefix = "64";
        Assert.True(vm.CanApply);
        Assert.All(vm.Assignment.Rows, r => Assert.Equal("Ready", r.StatusText));

        var payload = vm.Payload!;
        Assert.Equal("2001:db8::61", payload.Devices[devices[1].Id].Ipv6Address);
        var plan = NetworkPlanner.Build(payload, devices[1].Id, Fixture.Modern, Current, devices[1].Address);
        Assert.Contains("2001:db8::61/64", Uri.UnescapeDataString(plan.Steps.Single(s => s.Kind == StepKind.Ipv6).Requests[0].Body), StringComparison.Ordinal);

        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Keep);
        Assert.False(vm.Assignment.ShowIpv6);
    }

    [Fact]
    public void Static_ipv6_for_one_device_uses_the_field()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);
        Assert.True(vm.ShowIpv6Address);
        Assert.Equal("Enter an IPv6 address.", vm.ErrorOf(nameof(vm.Ipv6Address)));

        vm.Ipv6Address = "::1";
        Assert.Equal("Loopback address.", vm.ErrorOf(nameof(vm.Ipv6Address)));

        vm.Ipv6Address = "2001:db8::10";
        Assert.False(vm.HasErrors);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Address_check_marks_ping_answers_in_the_ipv4_and_ipv6_columns()
    {
        var devices = Devices(2);
        var ctx = new FakeDialogContext((_, _) => Task.FromResult<string?>(Current.ToJson()))
        {
            Answer = (_, payload) =>
            {
                var request = AddressCheckRequest.Parse(payload);
                return new AddressCheckResponse([], [.. request.Addresses.Select(a => new AddressStatus(a, InUse: a is "10.0.0.49" or "2001:db8::61", AnswersPing: true))]).ToJson();
            },
        };
        var vm = new NetworkSettingsViewModel(devices);
        await vm.LoadCurrentAsync(ctx, CancellationToken.None);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);
        vm.Assignment.Rows[1].NewAddress = "10.0.0.60";
        vm.Assignment.Rows[0].NewIpv6Address = "2001:db8::60";
        vm.Assignment.Rows[1].NewIpv6Address = "2001:db8::61";

        await vm.Assignment.CheckAsync(CancellationToken.None);

        Assert.Null(vm.Assignment.Rows[0].Conflict); // its own address
        Assert.Equal("In use (answers ping)", vm.Assignment.Rows[1].Ipv6Conflict);
        Assert.Equal("IPv6: In use (answers ping)", vm.Assignment.Rows[1].StatusText);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Dhcp_switch_is_confirmed_on_apply()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Dhcp);
        Assert.True(vm.HasWarning);
        Assert.Contains("DHCP server", vm.ReachabilityWarning, StringComparison.Ordinal);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Ipv6_change_warns_for_devices_reached_over_ipv6()
    {
        var vm = new NetworkSettingsViewModel([new FakeDevice(Guid.NewGuid(), "2001:db8::48")]);
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Disabled);
        Assert.True(vm.HasWarning);

        var v4 = new NetworkSettingsViewModel(Devices(1));
        v4.SelectedIpv6 = v4.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Disabled);
        Assert.False(v4.HasWarning);
        Assert.True(v4.CanApply);
    }

    [Fact]
    public void Cancel_returns_no_payload()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        bool? closed = null;
        vm.CloseRequested += (_, apply) => closed = apply;
        vm.CancelCommand.Execute(null);
        Assert.False(closed);
        Assert.Null(vm.ResultJson);
    }

    [Fact]
    public async Task Loads_current_settings_through_the_plugin_query()
    {
        var devices = Devices(2);
        var ctx = new FakeDialogContext((_, _) => Task.FromResult<string?>(Current.ToJson()));
        var vm = new NetworkSettingsViewModel(devices);

        await vm.LoadCurrentAsync(ctx, CancellationToken.None);

        Assert.Equal((devices[0].Id, "getNetworkInfo"), Assert.Single(ctx.Queries));
        Assert.Equal("10.0.0.138", vm.Ipv4Gateway);
        Assert.Equal(vm.DeviceSummary, vm.DevicesDescription);
        Assert.Equal(string.Empty, vm.HostNameText); // a single name is never prefilled for several devices
    }

    [Fact]
    public async Task Failed_prefill_keeps_the_dialog_usable()
    {
        var ctx = new FakeDialogContext((_, _) => throw new NotSupportedException("Plugin queries are not available yet."));
        var vm = new NetworkSettingsViewModel(Devices(1));

        await vm.LoadCurrentAsync(ctx, CancellationToken.None);

        Assert.Contains("could not be read", vm.PrefillStatus, StringComparison.Ordinal);
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Dhcp);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Dialog_payload_passes_server_validation_and_planning()
    {
        var devices = Devices(2);
        var vm = new NetworkSettingsViewModel(devices);
        ConfirmWith(vm, answer: true);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.60";
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.138";
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Static);
        vm.DnsPrimary = "10.0.0.2";
        vm.DnsSearch = "example.com, lab.example.com";
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam-{serial}";
        await vm.ApplyCommand.ExecuteAsync(null);

        var payload = NetworkPayload.Parse(vm.ResultJson);
        var plan = NetworkPlanner.Build(payload, devices[1].Id, Fixture.Modern, Current, devices[1].Address);
        Assert.Equal([StepKind.HostName, StepKind.Dns, StepKind.Ipv4], plan.Steps.Select(s => s.Kind));
        Assert.Contains("\"staticHostname\":\"cam-accc8e000002\"", plan.Steps[0].Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"address\":\"10.0.0.61\"", plan.Steps[2].Requests[0].Body, StringComparison.Ordinal);
    }
}
