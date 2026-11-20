using Oadm.Plugins.Overlays.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>The dialog's view model: the list, modifiers at the caret, Read from a camera, the checks and the payload.</summary>
public sealed class ViewModelTests
{
    internal static OverlaysQueryResult Read(params TextOverlay[] overlays) =>
        new(true, null, new OverlayCapabilities(512, 16, 200, 8, 1, 1), overlays);

    internal static OverlaysDialogViewModel Dialog(IReadOnlyList<IDeviceInfo>? devices = null, OverlaysQueryResult? answer = null, IDeviceInfo? picked = null)
    {
        devices ??= [Fixtures.Camera(), Fixtures.Camera("10.0.0.49")];
        return new OverlaysDialogViewModel(devices, devices,
            (_, _, _) => Task.FromResult<string?>(OverlaysJson.Serialize(answer ?? Read())),
            _ => Task.FromResult<IDeviceInfo?>(picked ?? devices[0]));
    }

    [Fact]
    public void It_opens_with_one_empty_overlay_and_no_error_shown()
    {
        var vm = Dialog();
        var row = Assert.Single(vm.Rows);
        Assert.Null(row.ErrorOf(nameof(row.Text)));
        Assert.False(vm.CanApply);
        Assert.Equal(OverlayRules.EnterText, vm.ApplyBlockedReason);
        Assert.Equal("2 devices", vm.DevicesText);
        Assert.Equal((OverlayPosition.BottomLeft, OverlayColor.White, OverlayColor.Black), (row.SelectedPosition.Position, row.SelectedTextColor.Color, row.SelectedBackgroundColor.Color));
    }

    [Fact]
    public void Modifiers_go_in_at_the_caret_with_a_space_before_a_word()
    {
        var row = Dialog().Rows[0];
        row.Text = "Lobby";
        row.CaretIndex = 5;
        row.InsertCommand.Execute(OverlayModifiers.Date);
        Assert.Equal("Lobby %F", row.Text);
        Assert.Equal(8, row.CaretIndex);
        row.InsertCommand.Execute(OverlayModifiers.Time);
        Assert.Equal("Lobby %F %X", row.Text);
        row.CaretIndex = 0;
        row.InsertCommand.Execute(OverlayModifiers.CameraName);
        Assert.Equal("#nLobby %F %X", row.Text);
        row.DynamicTextActions[2].Command.Execute(null);
        Assert.Equal("#n #D3Lobby %F %X", row.Text);
    }

    [Fact]
    public void Errors_show_below_their_field_and_block_apply()
    {
        var vm = Dialog();
        var row = vm.Rows[0];
        row.Text = "x";
        row.SizeText = "big";
        Assert.Equal(OverlayRowViewModel.WholeNumber, row.ErrorOf(nameof(row.SizeText)));
        Assert.False(vm.CanApply);
        row.SizeText = "5000";
        Assert.Equal(OverlayRules.SizeRange(1, 999), row.ErrorOf(nameof(row.SizeText))); // the widest range until a camera was read
        Assert.False(vm.CanApply);
        row.SizeText = "300";
        Assert.Null(row.ErrorOf(nameof(row.SizeText)));
        Assert.True(vm.CanApply);

        row.Text = string.Empty;
        Assert.Equal(OverlayRules.EnterText, row.ErrorOf(nameof(row.Text)));
        Assert.Equal(OverlayRules.EnterText, vm.ApplyBlockedReason);
    }

    [Fact]
    public async Task The_limits_of_the_first_camera_check_the_size()
    {
        var vm = Dialog();
        await vm.LoadAsync(CancellationToken.None);
        vm.Rows[0].Text = "x";
        vm.Rows[0].SizeText = "300";
        Assert.Equal(OverlayRules.SizeRange(16, 200), vm.Rows[0].ErrorOf(nameof(OverlayRowViewModel.SizeText)));
        Assert.Equal("16 to 200; empty: the camera's default", vm.SizeTip);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Read_from_a_camera_fills_the_list_with_its_overlays_and_keeps_a_custom_place()
    {
        var custom = new TextOverlay { Text = "Lobby", Position = OverlayPosition.Custom, CustomPosition = [-0.25, -0.5] };
        var vm = Dialog(answer: Read(new TextOverlay { Text = "%F\n%X" }, custom));
        await vm.ReadFromCameraCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("Read 2 overlays from AXIS P3265-V (10.0.0.48).", vm.ReadStatus);
        Assert.Equal("Custom position", vm.Rows[1].SelectedPosition.Name);
        Assert.DoesNotContain(vm.Rows[0].Positions, p => p.Position == OverlayPosition.Custom);
        var payload = OverlaysJson.ParsePayload(vm.BuildPayload());
        Assert.Equal("%F\n%X", payload.Overlays[0].Text);
        Assert.Equal([-0.25, -0.5], payload.Overlays[1].CustomPosition);
    }

    [Fact]
    public void The_list_holds_at_most_eight_and_replace_may_be_empty()
    {
        var vm = Dialog();
        for (var i = 0; i < 10; i++)
        {
            vm.AddOverlayCommand.Execute(null);
        }

        Assert.Equal(OverlayRules.MaxOverlays, vm.Rows.Count);
        Assert.False(vm.AddOverlayCommand.CanExecute(null));
        Assert.Equal(OverlayRules.TooMany, vm.AddOverlayTip);

        while (vm.Rows.Count > 0)
        {
            vm.Rows[0].RemoveCommand.Execute(null);
        }

        Assert.Equal(OverlayRules.AddOne, vm.ErrorOf(OverlaysDialogViewModel.OverlaysField));
        Assert.False(vm.CanApply);
        vm.IsReplace = true;
        Assert.True(vm.CanApply);
        Assert.Equal("Replace overlays", vm.ApplyText);
        Assert.Equal(OverlaysMode.Replace, OverlaysJson.ParsePayload(vm.BuildPayload()).Mode);
    }

    [Fact]
    public void Remove_matching_hides_everything_but_the_text()
    {
        var vm = Dialog();
        vm.Rows[0].Text = "Lobby";
        vm.Rows[0].SizeText = "5000";
        vm.IsRemove = true;
        Assert.False(vm.Rows[0].ShowStyle);
        Assert.Equal("Remove overlays", vm.ApplyText);
    }

    [Fact]
    public void Devices_without_the_api_are_counted()
    {
        IReadOnlyList<IDeviceInfo> devices = [Fixtures.Camera(), new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] }];
        var vm = Dialog(devices);
        Assert.Equal("2 devices: 1 compatible, 1 not supported", vm.DevicesText);

        var none = Dialog([new FakeDevice { Apis = [] }]);
        none.Rows[0].Text = "x";
        Assert.False(none.CanApply);
        Assert.Equal("None of the selected devices supports text overlays.", none.ApplyBlockedReason);
    }

    [Fact]
    public void Apply_closes_with_the_payload_and_cancel_with_nothing()
    {
        var vm = Dialog();
        string? closed = "unset";
        vm.CloseRequested += (_, payload) => closed = payload;
        vm.Rows[0].Text = "Lobby";
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Lobby", Assert.Single(OverlaysJson.ParsePayload(closed).Overlays).Text);
        vm.CancelCommand.Execute(null);
        Assert.Null(closed);
    }
}
