using Grpc.Core;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.LiveView;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>Zoom and focus of the live view (OpticsViewModel) and the context menu's "OADM > RTSP port".</summary>
public sealed class OpticsAndRtspPortTests
{
    private static OpticsState Lens() => new()
    {
        Supported = true,
        HasZoom = true,
        HasFocus = true,
        MaxMagnification = 2.7057,
        Magnification = 2.38,
        FocusPosition = 0.5367,
        FocusWindow = new OpticsWindow { X = 0.18, Y = 0.11, Width = 0.53, Height = 0.82 },
    };

    [Fact]
    public async Task The_bar_shows_the_cameras_values_and_only_for_cameras_with_zoom_or_focus()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetOpticsAsync("cam", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Lens());
        api.GetOpticsAsync("fixed", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new OpticsState());
        using var vm = new OpticsViewModel(api) { Debounce = TimeSpan.Zero };

        await vm.LoadAsync("cam");
        Assert.True(vm.IsVisible);
        Assert.Equal(2.7057, vm.MaxZoom);
        Assert.Equal(2.38, vm.Zoom);
        Assert.Equal(53.67, vm.Focus, 2);
        Assert.Equal(new Avalonia.Rect(0.18, 0.11, 0.53, 0.82), vm.Area);
        await api.DidNotReceiveWithAnyArgs().SetOpticsAsync(default!, default); // reading never sends values back

        vm.CanControl = false; // a role without "Optics Control"
        Assert.False(vm.IsVisible);
        vm.CanControl = true;

        await vm.LoadAsync("fixed");
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsError);
        api.GetOpticsAsync("broken", Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "down")));
        await vm.LoadAsync("broken");
        Assert.False(vm.IsVisible); // no controls, no error
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Sliders_send_their_value_once_they_rest_and_take_the_cameras_answer()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetOpticsAsync("cam", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Lens());
        var sent = new List<SetOpticsRequest>();
        api.SetOpticsAsync(Arg.Do<SetOpticsRequest>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(c => { var s = Lens(); var r = c.Arg<SetOpticsRequest>(); if (r.HasMagnification) { s.Magnification = r.Magnification; } return s; });
        using var vm = new OpticsViewModel(api) { Debounce = TimeSpan.FromMilliseconds(150) };
        await vm.LoadAsync("cam");

        vm.Zoom = 1.2;
        vm.Zoom = 1.6;
        vm.Zoom = 2.0;
        Assert.Empty(sent); // still moving
        await TestSupport.WaitUntilAsync(() => sent.Count == 1);
        Assert.Equal(2.0, sent[0].Magnification);
        Assert.Equal("cam", sent[0].DeviceId);
        Assert.False(sent[0].HasFocusPosition);

        vm.Focus = 40;
        await TestSupport.WaitUntilAsync(() => sent.Count == 2);
        Assert.Equal(0.4, sent[1].FocusPosition, 6);
    }

    [Fact]
    public async Task Autofocus_and_the_autofocus_area_wait_for_the_camera_and_show_its_refusal()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetOpticsAsync("cam", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Lens());
        var sent = new List<SetOpticsRequest>();
        api.SetOpticsAsync(Arg.Do<SetOpticsRequest>(sent.Add), Arg.Any<CancellationToken>()).Returns(Lens());
        using var vm = new OpticsViewModel(api) { Debounce = TimeSpan.Zero };
        await vm.LoadAsync("cam");

        await vm.AutofocusCommand.ExecuteAsync(null);
        Assert.True(sent[^1].Autofocus);
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.StatusText);

        vm.EditAreaCommand.Execute(null);
        Assert.True(vm.IsEditingArea);
        vm.Area = new Avalonia.Rect(0.1, 0.2, 0.3, 0.4);
        await vm.ApplyAreaCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditingArea);
        Assert.Equal((0.1, 0.2, 0.3, 0.4), (sent[^1].FocusWindow.X, sent[^1].FocusWindow.Y, sent[^1].FocusWindow.Width, sent[^1].FocusWindow.Height));

        // Cancel puts the camera's area back.
        vm.EditAreaCommand.Execute(null);
        vm.Area = new Avalonia.Rect(0, 0, 0.1, 0.1);
        vm.CancelAreaCommand.Execute(null);
        Assert.Equal(new Avalonia.Rect(0.18, 0.11, 0.53, 0.82), vm.Area);

        api.SetOpticsAsync(Arg.Any<SetOpticsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RpcException(new Status(StatusCode.FailedPrecondition, FakeOadmApi.FakeFocusNotFound)));
        await vm.ResetFocusCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Equal(FakeOadmApi.FakeFocusNotFound, vm.StatusText);
    }

    [Fact]
    public async Task A_ptz_camera_moves_by_the_circle_zooms_by_the_wheel_and_goes_to_presets()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetPtzAsync("ptz", 2, Arg.Any<CancellationToken>()).Returns(new PtzState
        {
            Supported = true, CanMove = true, CanZoom = true, CanCenter = true, CanGoHome = true, Presets = { "Home", "Gate" }, Zoom = 1,
        });
        api.GetPtzAsync("dome", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PtzState());
        var sent = new List<MovePtzRequest>();
        api.MovePtzAsync(Arg.Do<MovePtzRequest>(r => sent.Add(r.Clone())), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var vm = new PtzViewModel(api) { Debounce = TimeSpan.Zero, RefreshDelay = TimeSpan.FromHours(1), RepeatInterval = TimeSpan.FromHours(1) };

        await vm.LoadAsync("dome", 1);
        Assert.False(vm.IsVisible);
        await vm.LoadAsync("ptz", 2);
        Assert.True(vm.IsVisible);
        Assert.Equal(["Home", "Gate"], vm.Presets);
        Assert.Empty(sent); // reading never moves

        // The circle: speed by the distance, a small offset does not move, release stops.
        vm.BeginDrag();
        vm.DragTo(0.03, 0);
        Assert.Empty(sent);
        vm.DragTo(0.5, -1.2);
        vm.EndDrag();
        await TestSupport.WaitUntilAsync(() => sent.Count == 2);
        Assert.Equal((PtzMove.Continuous, 50.0, 100.0), (sent[0].Move, sent[0].X, sent[0].Y)); // up on screen = tilt up
        Assert.Equal(PtzMove.Stop, sent[1].Move);
        Assert.All(sent, r => Assert.Equal((2, "ptz"), (r.Camera, r.DeviceId)));

        // Wheel notches: here without a wait, so each notch goes out; together two notches of zoom.
        vm.Wheel(1);
        vm.Wheel(1);
        await TestSupport.WaitUntilAsync(() => sent.Where(r => r.Move == PtzMove.ZoomBy).Sum(r => r.Value) == 2 * PtzViewModel.WheelStep);
        sent.Clear();

        vm.CenterAt(0.25, 0.75);
        vm.SelectedPreset = "Gate";
        await vm.HomeCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => sent.Count == 3);
        Assert.Equal((PtzMove.Center, 0.25, 0.75), (sent[0].Move, sent[0].X, sent[0].Y));
        Assert.Equal((PtzMove.Preset, "Gate"), (sent[1].Move, sent[1].Preset));
        Assert.Equal(PtzMove.Home, sent[2].Move);

        api.MovePtzAsync(Arg.Any<MovePtzRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RpcException(new Status(StatusCode.FailedPrecondition, "The camera refused: goto preset: No such preset position found")));
        vm.SelectedPreset = "Home";
        await TestSupport.WaitUntilAsync(() => vm.StatusText.Length > 0);
        Assert.StartsWith("The camera refused", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drag_sends_one_move_at_a_time_and_the_stop_always_comes_last()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetPtzAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PtzState
        {
            Supported = true, CanMove = true, CanZoom = true, Presets = { "Gate" }, Zoom = 1,
        });
        var sent = new List<MovePtzRequest>();
        var slow = new TaskCompletionSource();
        api.MovePtzAsync(Arg.Any<MovePtzRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            lock (sent)
            {
                sent.Add(call.Arg<MovePtzRequest>().Clone());
                return sent.Count == 1 ? slow.Task : Task.CompletedTask; // the camera answers the first move late
            }
        });
        using var vm = new PtzViewModel(api) { Debounce = TimeSpan.Zero, RefreshDelay = TimeSpan.FromHours(1), RepeatInterval = TimeSpan.FromHours(1) };
        await vm.LoadAsync("ptz", 1);

        vm.BeginDrag();
        vm.DragTo(0.5, 0);
        vm.DragTo(0.6, 0);
        vm.DragTo(0.7, 0);
        vm.EndDrag();
        Assert.Single(sent); // nothing more while the first is on its way
        slow.SetResult();
        await TestSupport.WaitUntilAsync(() => sent.Count == 2);
        await Task.Delay(50);
        Assert.Equal([PtzMove.Continuous, PtzMove.Stop], sent.Select(r => r.Move)); // the moves in between are skipped

        // The same preset again goes there again; wheel notches never carry over to another camera.
        sent.Clear();
        vm.SelectedPreset = "Gate";
        Assert.Null(vm.SelectedPreset);
        vm.SelectedPreset = "Gate";
        await TestSupport.WaitUntilAsync(() => sent.Count(r => r.Move == PtzMove.Preset) == 2);
        using var wheel = new PtzViewModel(api) { Debounce = TimeSpan.FromHours(1), RefreshDelay = TimeSpan.FromHours(1) };
        await wheel.LoadAsync("ptz", 1);
        wheel.Wheel(3);
        await wheel.LoadAsync("ptz", 2); // another source before the wheel rested
        sent.Clear();
        var next = new PtzViewModel(api) { Debounce = TimeSpan.Zero, RefreshDelay = TimeSpan.FromHours(1) };
        using (next)
        {
            await next.LoadAsync("ptz", 2);
        }

        Assert.DoesNotContain(sent, r => r.Move == PtzMove.ZoomBy);
    }

    [Fact]
    public async Task The_rtsp_port_dialog_reads_checks_and_saves_the_port_for_the_selection()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetRtspPortAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new RtspPortReply { Port = 554, Mixed = true });
        using var f = new DevicesFixture();
        f.SeedDevices(Video("d1", "10.0.0.48"), Video("d2", "10.0.0.49"));
        var vm = new RtspPortViewModel(api, [.. f.Store.Devices]);

        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal("", vm.PortText); // the devices differ
        Assert.Contains("different ports", vm.Hint, StringComparison.Ordinal);

        vm.PortText = "70000";
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Enter a port from 1 to 65535.", vm.ErrorOf(nameof(vm.PortText)));
        vm.PortText = " 8554 ";
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        await vm.SaveCommand.ExecuteAsync(null);
        await api.Received(1).SetRtspPortAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "d1", "d2" })), 8554, Arg.Any<CancellationToken>());
        Assert.True(closed);

        vm.UseDefaultCommand.Execute(null);
        Assert.Equal("554", vm.PortText);
        Assert.True(RtspPortViewModel.ParsePort("", out var empty) && empty == 554);
    }

    [Fact]
    public void The_oadm_submenu_shows_for_video_devices_and_a_role_with_optics_control()
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "tech", Role = UserRole.Operator, RoleId = FakeOadmApi.ViewerRoleId, RoleName = "Viewer", Permissions = { "devices.opticsControl" } }, "fake");
        using var f = new DevicesFixture(session: session);
        var audio = TestSupport.Device("d2", "ACCC8E000002", "10.0.0.2", "C1410");
        f.SeedDevices(Video("d1", "10.0.0.1"), audio);

        f.Select("d2");
        f.Devices.RebuildContextMenu();
        Assert.DoesNotContain(f.Devices.ContextMenuEntries, e => e.Header == DevicesViewModel.OadmMenuHeader);

        f.Select("d1");
        f.Devices.RebuildContextMenu();
        var oadm = Assert.Single(f.Devices.ContextMenuEntries, e => e.Header == DevicesViewModel.OadmMenuHeader);
        Assert.Equal([DevicesViewModel.RtspPortMenuHeader], oadm.Items!.Select(i => i.Header));

        session.Update(new UserInfo { UserName = "tech", Role = UserRole.Operator, RoleId = FakeOadmApi.ViewerRoleId, RoleName = "Viewer", Permissions = { "devices.liveView" } });
        Assert.DoesNotContain(f.Devices.ContextMenuEntries, e => e.Header == DevicesViewModel.OadmMenuHeader);
        Assert.False(f.Devices.LiveView.Video.Optics.CanControl);
    }

    private static Device Video(string id, string address)
    {
        var device = TestSupport.Device(id, "ACCC8E00000" + id[^1], address, "P3265-V");
        device.HasVideo = true;
        return device;
    }
}
