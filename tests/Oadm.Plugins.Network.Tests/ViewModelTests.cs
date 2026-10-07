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
        Assert.False(vm.ApplyCommand.CanExecute(null) && vm.CanApply);
    }

    [Fact]
    public void Static_ipv6_is_offered_only_for_one_device()
    {
        Assert.Contains(new NetworkSettingsViewModel(Devices(1)).Ipv6Choices, c => c.Value == Ipv6Choice.Static);
        Assert.DoesNotContain(new NetworkSettingsViewModel(Devices(3)).Ipv6Choices, c => c.Value == Ipv6Choice.Static);
    }

    [Fact]
    public void Only_touched_sections_end_up_in_the_payload()
    {
        var devices = Devices(1);
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Dhcp);

        Assert.True(vm.CanApply);
        bool? closed = null;
        vm.CloseRequested += (_, apply) => closed = apply;
        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
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
        Assert.Contains("DHCP, 10.0.0.48 / 255.255.255.0, gateway 10.0.0.138", vm.CurrentIpv4Text, StringComparison.Ordinal);
        Assert.Equal("Current: disabled", vm.CurrentIpv6Text);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Multi_device_static_assigns_a_range_in_list_order_with_preview()
    {
        var devices = Devices(3);
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.100";
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.1";

        Assert.Equal("IP range", vm.Ipv4AddressLabel);
        Assert.True(vm.ShowPreview);
        Assert.Equal(["10.0.0.100", "10.0.0.101", "10.0.0.102"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.Equal(["10.0.0.48", "10.0.0.49", "10.0.0.50"], vm.Assignment.Rows.Select(r => r.CurrentAddress));
        Assert.False(vm.HasErrors);

        // Re-addressing: strong warning, Apply only after acknowledging it.
        Assert.True(vm.HasWarning);
        Assert.Contains("3 of 3 devices get a new IPv4 address", vm.ReachabilityWarning, StringComparison.Ordinal);
        Assert.False(vm.CanApply);
        vm.WarningAcknowledged = true;
        Assert.True(vm.CanApply);

        vm.ApplyCommand.Execute(null);
        var payload = NetworkPayload.Parse(vm.ResultJson);
        Assert.Equal(24, payload.Ipv4!.PrefixLength);
        Assert.Equal("10.0.0.101", payload.Devices[devices[1].Id].Ipv4Address);
    }

    [Fact]
    public void Range_that_does_not_fit_is_an_error()
    {
        var vm = new NetworkSettingsViewModel(Devices(3));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.253";
        vm.Ipv4Mask = "255.255.255.0";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.WarningAcknowledged = true;

        Assert.True(vm.HasErrors);
        Assert.Contains(vm.Errors, e => e.Contains("Not enough addresses: the IP range has 2 free addresses for 3 devices", StringComparison.Ordinal));
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
        Assert.Contains(vm.Errors, e => e.Contains("is the gateway address", StringComparison.Ordinal));
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void Range_syntax_of_adm_is_accepted_and_edits_survive_other_changes()
    {
        var vm = new NetworkSettingsViewModel(Devices(3));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.Ipv4Address = "10.0.0.10-11,10.0.0.20";
        Assert.Equal(["10.0.0.10", "10.0.0.11", "10.0.0.20"], vm.Assignment.Rows.Select(r => r.NewAddress));

        vm.Assignment.Rows[0].NewAddress = "10.0.0.30";
        vm.Ipv4Mask = "255.255.255.0"; // same network, the edit stays
        Assert.Equal(["10.0.0.30", "10.0.0.11", "10.0.0.20"], vm.Assignment.Rows.Select(r => r.NewAddress));
        vm.Ipv4Gateway = "10.0.0.2"; // suggested again around the edit
        Assert.Equal(["10.0.0.30", "10.0.0.10", "10.0.0.11"], vm.Assignment.Rows.Select(r => r.NewAddress));
        vm.WarningAcknowledged = true;
        Assert.True(vm.CanApply);
        Assert.Equal("10.0.0.30", vm.Payload!.Devices[vm.Assignment.Rows[0].Device.Id].Ipv4Address);

        vm.Ipv4Address = "10.0.0.50-60"; // a new range discards edits
        Assert.Equal(["10.0.0.50", "10.0.0.51", "10.0.0.52"], vm.Assignment.Rows.Select(r => r.NewAddress));
    }

    [Fact]
    public void Bad_mask_is_reported_once()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.60";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.Ipv4Mask = "255.0.255.0";
        Assert.Single(vm.Errors, e => e.Contains("subnet mask", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_name_template_without_tokens_is_a_duplicate_for_several_devices()
    {
        var vm = new NetworkSettingsViewModel(Devices(2));
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam";
        Assert.Contains(vm.Errors, e => e.Contains("assigned to 2 devices", StringComparison.Ordinal));

        vm.HostNameText = "cam-{n}";
        Assert.False(vm.HasErrors);
        Assert.Equal(["cam-1", "cam-2"], vm.Assignment.Rows.Select(r => r.NewHostName));
        Assert.Equal(["Unchanged", "Unchanged"], vm.Assignment.Rows.Select(r => r.NewAddress));
        Assert.False(vm.HasWarning);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Dhcp_switch_warns_that_the_address_may_change()
    {
        var vm = new NetworkSettingsViewModel(Devices(1));
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Dhcp);
        Assert.True(vm.HasWarning);
        Assert.Contains("DHCP server", vm.ReachabilityWarning, StringComparison.Ordinal);
        Assert.False(vm.CanApply);
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
        Assert.Contains("network-settings 1.38", vm.PrefillStatus, StringComparison.Ordinal);
        Assert.Equal(vm.DeviceSummary + " " + vm.PrefillStatus, vm.DevicesDescription);
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
    public void Dialog_payload_passes_server_validation_and_planning()
    {
        var devices = Devices(2);
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Address = "10.0.0.60";
        vm.Ipv4Mask = "24";
        vm.Ipv4Gateway = "10.0.0.138";
        vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Static);
        vm.DnsPrimary = "10.0.0.2";
        vm.DnsSearch = "example.com, lab.example.com";
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam-{serial}";
        vm.WarningAcknowledged = true;
        vm.ApplyCommand.Execute(null);

        var payload = NetworkPayload.Parse(vm.ResultJson);
        var plan = NetworkPlanner.Build(payload, devices[1].Id, Fixture.Modern, Current, devices[1].Address);
        Assert.Equal([StepKind.HostName, StepKind.Dns, StepKind.Ipv4], plan.Steps.Select(s => s.Kind));
        Assert.Contains("\"staticHostname\":\"cam-accc8e000002\"", plan.Steps[0].Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"address\":\"10.0.0.61\"", plan.Steps[2].Requests[0].Body, StringComparison.Ordinal);
    }
}
