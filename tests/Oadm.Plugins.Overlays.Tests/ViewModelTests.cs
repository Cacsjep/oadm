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

    internal static OverlaysDialogViewModel Dialog(IReadOnlyList<IDeviceInfo>? devices = null, OverlaysQueryResult? answer = null, IDeviceInfo? picked = null,
        OverlaysMode mode = OverlaysMode.Add)
    {
        devices ??= [Fixtures.Camera(), Fixtures.Camera("10.0.0.49")];
        return new OverlaysDialogViewModel(mode, devices, devices,
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
        Assert.Null(vm.UnsupportedText); // every selected camera is supported: no line
        Assert.Equal((OverlayPosition.BottomLeft, OverlayColor.White, OverlayColor.Black), (row.SelectedPosition.Position, row.SelectedTextColor.Color, row.SelectedBackgroundColor.Color));
    }

    [Fact]
    public void Modifiers_go_in_at_the_end_of_the_text_wherever_the_caret_is()
    {
        var row = Dialog().Rows[0];
        row.Text = "Camera";
        row.CaretIndex = 0; // the box lost its focus to the button: the caret says nothing
        row.InsertCommand.Execute(OverlayModifiers.CameraName);
        Assert.Equal("Camera#n", row.Text);
        Assert.Equal(8, row.CaretIndex);
        row.InsertCommand.Execute(OverlayModifiers.Date);
        Assert.Equal("Camera#n%F", row.Text);
        row.DynamicTextActions[2].Command.Execute(null);
        Assert.Equal("Camera#n%F#D3", row.Text);

        var empty = Dialog().Rows[0];
        empty.Text = string.Empty;
        empty.InsertCommand.Execute(OverlayModifiers.Time);
        Assert.Equal("%X", empty.Text);
    }

    [Fact]
    public void Text_is_black_white_or_red_a_cameras_own_transparent_text_stays_on_its_row()
    {
        var row = Dialog().Rows[0];
        Assert.Equal([OverlayColor.Black, OverlayColor.White, OverlayColor.Red], row.TextColors.Select(c => c.Color));
        Assert.Equal(5, OverlayRowViewModel.Colors.Count); // background and outline keep all five

        var read = new OverlayRowViewModel(() => OverlayLimits.Default, _ => { }, new TextOverlay { Text = "Lobby", TextColor = OverlayColor.SemiTransparent });
        Assert.Equal([OverlayColor.Black, OverlayColor.White, OverlayColor.Red, OverlayColor.SemiTransparent], read.TextColors.Select(c => c.Color));
        Assert.Equal(OverlayColor.SemiTransparent, read.SelectedTextColor.Color);
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
        var payload = OverlaysJson.ParseList(vm.BuildPayload());
        Assert.Equal("%F\n%X", payload.Overlays[0].Text);
        Assert.Equal([-0.25, -0.5], payload.Overlays[1].CustomPosition);
    }

    [Fact]
    public void The_list_holds_at_most_eight_and_needs_one()
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
        Assert.Equal(OverlayRules.AddOne, vm.ApplyBlockedReason);
    }

    [Theory]
    [InlineData(OverlaysMode.Add, "Add overlays", "Add overlays")]
    [InlineData(OverlaysMode.Replace, "Replace overlays", "Replace overlays")]
    [InlineData(OverlaysMode.RemoveMatching, "Remove matching overlays", "Remove overlays")]
    public void Each_menu_entry_opens_its_own_dialog(OverlaysMode mode, string title, string button)
    {
        var vm = Dialog(mode: mode);
        vm.Rows[0].Text = "Lobby";
        vm.Rows[0].SizeText = "5000";
        Assert.Equal((title, button), (vm.Title, vm.ApplyText));
        Assert.Equal(mode == OverlaysMode.Replace, vm.IsReplace); // its warning
        Assert.Equal(mode == OverlaysMode.RemoveMatching, vm.IsRemove); // the text only, the red button
        Assert.Equal(mode == OverlaysMode.RemoveMatching ? "Add text" : "Add overlay", vm.AddRowText); // Remove matching lists texts to find
        Assert.Equal(mode != OverlaysMode.RemoveMatching, vm.Rows[0].ShowStyle);
        Assert.Equal(mode == OverlaysMode.RemoveMatching, vm.CanApply); // a hidden size never blocks Remove matching
        Assert.Throws<ArgumentOutOfRangeException>(() => Dialog(mode: OverlaysMode.RemoveAll));
    }

    [Fact]
    public void Remove_all_asks_with_the_number_of_cameras()
    {
        IReadOnlyList<IDeviceInfo> devices = [Fixtures.Camera(), Fixtures.Camera("10.0.0.49"), new FakeDevice { Apis = [] }];
        Assert.Equal("Remove all text overlays of 2 cameras? Image overlays stay." + Environment.NewLine + "1 of 3 cameras is not supported.",
            RemoveAllOverlaysDialog.Question(devices));
        Assert.Equal("Remove all text overlays of 1 camera? Image overlays stay.", RemoveAllOverlaysDialog.Question([Fixtures.Camera()]));
    }

    [Fact]
    public void Devices_without_the_api_are_counted()
    {
        IReadOnlyList<IDeviceInfo> devices = [Fixtures.Camera(), new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] }];
        var vm = Dialog(devices);
        Assert.Equal("1 of 2 cameras is not supported.", vm.UnsupportedText);

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
        Assert.Equal("Lobby", Assert.Single(OverlaysJson.ParseList(closed).Overlays).Text);
        vm.CancelCommand.Execute(null);
        Assert.Null(closed);
    }
}
