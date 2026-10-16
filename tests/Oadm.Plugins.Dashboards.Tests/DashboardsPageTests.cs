using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Plugins.Dashboards.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>The page: what a widget shows, the empty states, editing with the widget panel, live values, roles, screenshots.</summary>
public sealed class DashboardsPageTests
{
    /// <summary>The page runs on the UI thread like in the client.</summary>
    private static readonly HeadlessUnitTestSession Ui = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

    private static async Task OnUi(Func<Task> test) =>
        Assert.True(await Ui.Dispatch(async () =>
        {
            await test();
            return true;
        }, CancellationToken.None));

    private static ValueSnapshot Snapshot(string typeId, bool on = false, string? stale = null, params (string Name, string Value)[] data) =>
        new(typeId, Guid.NewGuid(), on, data.ToDictionary(d => d.Name, d => d.Value), DateTimeOffset.Parse("2026-10-16T14:02:17Z", CultureInfo.InvariantCulture), stale);

    // ------------------------------------------------------------------ display

    [Fact]
    public void A_widget_shows_its_value_by_its_kind()
    {
        var number = new Widget { Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", Unit = "°C", Thresholds = [new(30, "Orange"), new(40, "Red")] };
        var shown = WidgetDisplay.Of(number, Snapshot(Types.Temperature, data: ("temperature", "41.5")));
        Assert.Equal((41.5.ToString("N1", CultureInfo.CurrentCulture), "°C", "Red", false), (shown.Text, shown.Unit, shown.Color, shown.Dimmed));
        Assert.StartsWith("41.5 at ", shown.ToolTip, StringComparison.Ordinal);
        Assert.Equal(20.ToString("N2", CultureInfo.CurrentCulture), WidgetDisplay.Of(number with { Decimals = 2 }, Snapshot(Types.Temperature, data: ("temperature", "20"))).Text);
        Assert.Equal("Text", WidgetDisplay.Of(number, Snapshot(Types.Temperature, data: ("temperature", "12"))).Color);

        var wrong = WidgetDisplay.Of(number, Snapshot(Types.Temperature, data: ("temperature", "hot")));
        Assert.Equal(("hot", true, DashboardTexts.NotANumber), (wrong.Text, wrong.Dimmed, wrong.Note)); // never a wrong reading

        var none = WidgetDisplay.Of(number, Snapshot(Types.Temperature));
        Assert.Equal((false, DashboardTexts.NoValue), (none.HasValue, none.Note));

        var old = WidgetDisplay.Of(number, Snapshot(Types.Temperature, stale: DashboardTexts.ConnectionLost, data: ("temperature", "20")));
        Assert.Equal((true, DashboardTexts.ConnectionLost), (old.Dimmed, old.Note));

        var onOff = new Widget { Kind = WidgetKind.OnOff, TypeId = Types.Gate, Field = DashboardsPluginInfo.ActiveField, OnText = "Open", OffText = "" };
        var on = WidgetDisplay.Of(onOff, Snapshot(Types.Gate, on: true));
        Assert.Equal(("status.ok", "Green", "Open"), (on.Icon, on.Color, on.Caption));
        var off = WidgetDisplay.Of(onOff, Snapshot(Types.Gate, on: false));
        Assert.Equal(("circle", "Neutral", (string?)null), (off.Icon, off.Color, off.Caption));
        var fromField = WidgetDisplay.Of(onOff with { Field = "active" }, Snapshot(Types.Gate, data: ("active", "yes")));
        Assert.Equal("status.ok", fromField.Icon);

        var text = WidgetDisplay.Of(new Widget { Kind = WidgetKind.Text, TypeId = Types.Plate, Field = "plate", Color = "Blue" }, Snapshot(Types.Plate, data: ("plate", "W-123")));
        Assert.Equal(("W-123", "Blue"), (text.Text, text.Color));
    }

    // ------------------------------------------------------------------ page

    private static (PluginHost Host, TestContext Ctx, DashboardsPageViewModel Vm, TestDevice Camera, TestDevice Io) Page()
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V", "Building A");
        var io = new TestDevice("10.0.0.120", "A9210") { Category = DeviceCategory.IoModule };
        var ctx = new TestContext(host, [camera, io]) { Video = new FakeVideoAccess() };
        return (host, ctx, new DashboardsPageViewModel(ctx), camera, io);
    }

    [Fact]
    public Task The_empty_state_says_what_to_do_first() => OnUi(async () =>
    {
        var (host, ctx, vm, _, _) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            Assert.Equal(DashboardTexts.NoDashboards, vm.EmptyText);
            Assert.False(vm.ShowOpenTriggers);
            Assert.Equal("Device triggers", vm.Events[0].Header); // device triggers first, grouped
            Assert.Contains(vm.Events, e => e.Event?.TypeId == Types.Unreachable);

            host.Feed.ChangeTypes(types => types.RemoveAll(t => t.SourceId == DashboardsPluginInfo.DeviceTriggersId));
            host.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await Headless.UntilAsync(() => vm.NoTriggers));
            Assert.Equal(DashboardTexts.NoTriggers, vm.EmptyText);
            Assert.True(vm.ShowOpenTriggers);
            await vm.OpenTriggersCommand.ExecuteAsync(null);
            Assert.Equal(["plugin:oadm.device-triggers"], ctx.Opened);

            ctx.TriggersOn = false;
            vm.ApplyCatalog(host.Plugin.Catalog());
            Assert.Equal(DashboardTexts.TriggersOff, vm.EmptyText);
            Assert.False(vm.ShowOpenTriggers);

            ctx.Denied.Add(PermissionKeys.Of(DashboardsPluginInfo.PluginId, DashboardsPluginInfo.ManageKey));
            ctx.TriggersOn = true;
            host.Feed.ChangeTypes(types => types.AddRange(Oadm.Plugins.Dashboards.Tests.Types.All()));
            vm.ApplyCatalog(host.Plugin.Catalog());
            ctx.RaisePermissionsChanged();
            Assert.Equal(DashboardTexts.NoDashboardsReadOnly, vm.EmptyText);
            Assert.False(vm.NewDashboardCommand.CanExecute(null));
        }
    });

    [Fact]
    public Task A_new_dashboard_gets_widgets_from_events_and_cameras_and_saves() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, io) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            host.Feed.Publish(Types.Temperature, io.Id, PublishedEventKind.Fired, ("temperature", "21.5"));
            vm.ApplyCatalog(host.Plugin.Catalog());
            vm.PickDevice = (_, _) => Task.FromResult<Guid?>(io.Id);

            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.Equal(("Dashboard 1", true), (tab.Name, tab.IsEditing));
            var temperature = vm.Events.First(e => e.Event?.TypeId == Types.Temperature).Event!;
            vm.ClickEvent(temperature);
            var widget = Assert.Single(tab.Widgets);
            var editor = tab.Editor!;
            Assert.Same(widget, editor.Target);
            Assert.Equal(("temperature", WidgetKind.Number), (widget.Widget.Field, widget.Widget.Kind)); // the number widget for a number
            Assert.Equal(["Number", "Text"], editor.Kinds.Select(k => k.Label));
            Assert.Equal("Automatic (Number)", editor.SelectedType!.Label);
            Assert.True(editor.NeedsDevice);
            Assert.False(vm.SaveDashboardCommand.CanExecute(null) && tab.WidgetList().All(w => w.DeviceId is not null));

            await editor.ChooseDeviceCommand.ExecuteAsync(null);
            Assert.Equal(io.Id, widget.Widget.DeviceId);
            Assert.Equal("Temperature · 10.0.0.120", widget.Widget.Name); // the default name follows the device
            editor.ShowName = false;
            Assert.False(widget.Widget.ShowName);
            editor.ShowName = true;
            editor.Unit = "°C";
            editor.AddThresholdCommand.Execute(null);
            editor.Thresholds[0].ValueText = "30";
            Assert.Equal([new Threshold(30, "Amber")], widget.Widget.Thresholds);
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Text);
            Assert.Equal(WidgetKind.Text, widget.Widget.Kind);
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Number);

            // The value of the open tab comes at once and then live.
            Assert.True(await Headless.UntilAsync(() => widget.Display.Text is not null));
            Assert.Equal(21.5.ToString("N1", CultureInfo.CurrentCulture), widget.Display.Text);
            host.Feed.Publish(Types.Temperature, io.Id, PublishedEventKind.Fired, ("temperature", "35"));
            host.Tick();
            Assert.True(await Headless.UntilAsync(() => widget.Display.Color == "Amber"));

            // A camera adds a live video.
            vm.ClickCamera(camera);
            var video = tab.Widgets[1];
            Assert.Equal((WidgetKind.Video, (Guid?)camera.Id), (video.Widget.Kind, video.Widget.DeviceId));
            Assert.Equal(camera.Id, ((FakeVideo)video.Video!).Shown);
            Assert.True(tab.Editor!.IsVideo);

            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.False(tab.IsEditing);
            Assert.Null(tab.Editor);
            var saved = Assert.Single((await host.Plugin.ListAsync(CancellationToken.None)).Dashboards);
            Assert.Equal(["Temperature · 10.0.0.120", "10.0.0.48"], saved.Widgets.Select(w => w.Name));
            Assert.Equal("°C", saved.Widgets[0].Unit);
            Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1));
        }
    });

    [Fact]
    public Task A_widget_is_copied_for_other_devices_with_their_names() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, io) = Page();
        using (host)
        using (vm)
        {
            var ports = Enumerable.Range(1, 30).Select(i => (IDeviceInfo)new TestDevice("10.0.1." + i, "A9210") { Category = DeviceCategory.IoModule }).ToList();
            ctx.Items.AddRange(ports);
            ctx.RaiseDevicesChanged();
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.PickDevice = (_, _) => Task.FromResult<Guid?>(io.Id);
            Func<IDeviceInfo, string?>? reasons = null;
            vm.PickDevices = (_, unavailable) =>
            {
                reasons = unavailable;
                return Task.FromResult<IReadOnlyList<Guid>?>([.. ports.Take(3).Select(d => d.Id), io.Id]);
            };

            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            vm.ClickEvent(vm.Events.First(e => e.Event?.TypeId == Types.Gate).Event!);
            var editor = tab.Editor!;
            Assert.False(editor.CanCopy); // no device yet
            await editor.ChooseDeviceCommand.ExecuteAsync(null);
            editor.OnText = "Open";
            Assert.True(editor.CanCopy);
            await editor.CopyForDevicesCommand.ExecuteAsync(null);

            Assert.Equal(DashboardTexts.OwnDevice, reasons!(io)); // the widget's own device is not offered
            Assert.Null(reasons(ports[0]));
            Assert.Equal(4, tab.Widgets.Count); // the own device is left out
            var copies = tab.Widgets.Skip(1).ToList();
            Assert.Equal(["Active · 10.0.1.1", "Active · 10.0.1.2", "Active · 10.0.1.3"], copies.Select(w => w.Widget.Name));
            Assert.All(copies, w => Assert.Equal(("Open", WidgetKind.OnOff, Types.Gate, 4, 4), (w.Widget.OnText, w.Widget.Kind, w.Widget.TypeId, w.Widget.Width, w.Widget.Height)));
            Assert.Equal(copies.Select(w => (Guid?)w.Device!.Id), ports.Take(3).Select(d => (Guid?)d.Id));
            Assert.Null(DashboardRules.WidgetsProblem(tab.WidgetList()));
            Assert.Null(tab.Error);

            // More than fit: as many as fit, and why not the rest (20 x 20 holds 25 widgets of 4 x 4).
            vm.PickDevices = (_, _) => Task.FromResult<IReadOnlyList<Guid>?>([.. ports.Select(d => d.Id)]);
            await editor.CopyForDevicesCommand.ExecuteAsync(null);
            Assert.Equal(25, tab.Widgets.Count);
            Assert.Equal(DashboardRules.CopiesAdded(21, 30, DashboardRules.NoRoom), tab.Error);

            // A camera's live video goes to cameras only.
            vm.NewDashboardCommand.Execute(null);
            vm.ClickCamera(camera);
            Assert.Equal(DashboardTexts.NoVideo, (await CopyReason(vm, vm.ActiveTab!.Widgets[^1]))(io));
        }
    });

    private static async Task<Func<IDeviceInfo, string?>> CopyReason(DashboardsPageViewModel vm, WidgetViewModel widget)
    {
        Func<IDeviceInfo, string?>? reasons = null;
        vm.PickDevices = (_, unavailable) =>
        {
            reasons = unavailable;
            return Task.FromResult<IReadOnlyList<Guid>?>(null);
        };
        await vm.PickCopyDevicesAsync(widget);
        return reasons!;
    }

    [Fact]
    public Task A_widget_of_a_device_event_needs_a_device_and_moves_stay_inside() => OnUi(async () =>
    {
        var (host, _, vm, _, _) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.True(tab.DropEvent(vm.Events.First(e => e.Event?.TypeId == Types.Gate).Event!, 10, 10));
            var widget = tab.Widgets[0];
            Assert.Equal(DashboardsPluginInfo.ActiveField, widget.Widget.Field);
            Assert.Equal(WidgetKind.OnOff, widget.Widget.Kind);
            Assert.Equal(4, widget.Widget.Width); // a dropped value takes up to 4 x 4

            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Equal(DashboardRules.NoDevice, tab.Error);

            Assert.True(tab.TryFit(widget, new(18, 18, 6, 6)));
            Assert.Equal((14, 14), (widget.Rect.X, widget.Rect.Y)); // clamped inside
            Assert.True(tab.DropEvent(vm.Events.First(e => e.Event?.TypeId == Types.ServerFlag).Event!, 0, 0));
            Assert.False(tab.TryFit(tab.Widgets[1], new(14, 14, 2, 2))); // onto the other
            Assert.Null(tab.Editor!.ShowDevice ? "device" : null); // a server event has no device

            widget.RemoveCommand.Execute(null);
            Assert.Single(tab.Widgets);
        }
    });

    [Fact]
    public Task Another_users_change_and_delete_reach_open_tabs() => OnUi(async () =>
    {
        var (host, ctx, vm, _, io) = Page();
        using (host)
        using (vm)
        {
            var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Hall", 0,
                [new Widget { Id = "t", Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", DeviceId = io.Id }]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1));
            vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
            var tab = vm.ActiveTab!;
            Assert.True(await Headless.UntilAsync(() => host.Plugin.WatchedCount == 1)); // the open tab's value is watched

            await host.Plugin.SaveAsync(new SaveDashboardRequest(saved.Id, "Hall", saved.Version, []), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => tab.Widgets.Count == 0));
            Assert.True(await Headless.UntilAsync(() => host.Plugin.WatchedCount == 0));

            // A device OADM no longer manages keeps its widget and says so.
            var reopened = await host.Plugin.SaveAsync(new SaveDashboardRequest(saved.Id, "Hall", saved.Version + 1,
                [new Widget { Id = "t", Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", DeviceId = io.Id }]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => tab.Widgets.Count == 1));
            ctx.Items.Remove(io);
            ctx.RaiseDevicesChanged();
            Assert.Null(tab.Widgets[0].Content);
            Assert.StartsWith("Removed device", tab.Widgets[0].Message, StringComparison.Ordinal);

            await host.Plugin.DeleteAsync(reopened.Id, CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Tabs.Count == 0 && vm.Dashboards.Count == 0));
        }
    });

    [Fact]
    public Task A_viewer_sees_no_pictures_and_cannot_change() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, _) = Page();
        using (host)
        using (vm)
        {
            await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Cams", 0, [new Widget { Id = "v", Kind = WidgetKind.Video, DeviceId = camera.Id }]), CancellationToken.None);
            ctx.Denied.Add(HostPermissionKeys.DevicesLiveView);
            ctx.Denied.Add(PermissionKeys.Of(DashboardsPluginInfo.PluginId, DashboardsPluginInfo.ManageKey));
            ctx.RaisePermissionsChanged();
            Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1));
            vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
            var widget = vm.ActiveTab!.Widgets[0];
            Assert.Null(widget.Video);
            Assert.Equal(DashboardTexts.NotAllowed, widget.Message);
            Assert.False(vm.EditDashboardCommand.CanExecute(vm.Dashboards[0]));
        }
    });

    [Fact]
    public Task Fake_mode_speaks_the_plugins_contract() => OnUi(async () =>
    {
        using var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.Zero);
        var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new TestDevice(d.Address, d.Model) { Id = Guid.Parse(d.Id) }).ToList();
        var ctx = new FakeApiContext(api, devices);
        using var vm = new DashboardsPageViewModel(ctx);
        Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1 && vm.HasEvents));
        Assert.Equal(3, vm.Events.Count(e => e.IsEvent));
        vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
        var widgets = vm.ActiveTab!.Widgets;
        Assert.Contains(widgets, w => w.Widget.Kind == WidgetKind.OnOff);
        Assert.True(await Headless.UntilAsync(() => widgets.Where(w => w.Widget.IsValue).All(w => w.Display.HasValue)));
        Assert.Equal("°C", widgets.First(w => w.Widget.Kind == WidgetKind.Number).Display.Unit);
    });

    /// <summary>A page context backed by fake mode's API (what <c>--fake</c> shows).</summary>
    private sealed class FakeApiContext(Oadm.Client.Api.FakeOadmApi api, List<IDeviceInfo> devices) : Oadm.Sdk.Client.ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => devices;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(DashboardsPluginInfo.PluginId, method, payloadJson, ct);

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var item in api.WatchCorePluginAsync(DashboardsPluginInfo.PluginId, ct))
            {
                yield return new PluginEvent(item.Topic, item.PayloadJson);
            }
        }
    }

    // ------------------------------------------------------------------ headless

    [Fact]
    public async Task The_page_shows_widgets_and_the_widget_panel()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var done = await session.Dispatch(async () =>
        {
            // The fake mode's devices and recorded video, like the client in fake mode.
            using var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new TestDevice(d.Address, d.Model, [.. d.Tags])
            {
                Id = Guid.Parse(d.Id),
                Category = (DeviceCategory)(int)d.Category,
                Status = DeviceStatus.Ok,
            }).ToList();
            var camera = devices.First(d => d.HasVideo);
            var io = devices.First(d => !d.HasVideo);
            var gate = devices.Last(d => !d.HasVideo);
            var host = new PluginHost();
            var access = new Oadm.Client.LiveView.LiveVideoAccess(api, new Oadm.Client.LiveView.FfmpegVideoDecoderFactory(), new Oadm.Client.Infrastructure.AvaloniaUiDispatcher(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.LiveView.LiveVideoAccess>.Instance, new Oadm.Client.LiveView.LiveVideoSettings());
            var ctx = new TestContext(host, devices) { Video = access };
            using (host)
            {
                host.Feed.Publish(Types.Temperature, io.Id, PublishedEventKind.Fired, ("temperature", "41.5"));
                host.Feed.Publish(Types.Gate, gate.Id, PublishedEventKind.On, ("port", "1"));
                host.Feed.Publish(Types.Plate, io.Id, PublishedEventKind.Fired, ("plate", "W-12345A"), ("speed", "48"));
                host.Feed.Publish(Types.Temperature, gate.Id, PublishedEventKind.Fired, ("temperature", "18.25"));
                host.Feed.Publish(Types.TemperatureLost, gate.Id, PublishedEventKind.On, ("reason", "Timeout"));
                await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Building A", 0,
                [
                    new Widget { Id = "t", Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", DeviceId = io.Id, Name = "Server room", Unit = "°C", X = 0, Y = 0, Width = 6, Height = 6, Thresholds = [new(30, "Orange"), new(40, "Red")] },
                    new Widget { Id = "g", Kind = WidgetKind.OnOff, TypeId = Types.Gate, Field = DashboardsPluginInfo.ActiveField, DeviceId = gate.Id, Name = "Gate", OnText = "Open", OffText = "Closed", OnIcon = "lockOpen", OnColor = "Amber", OffIcon = "lock", X = 6, Y = 0, Width = 4, Height = 6 },
                    new Widget { Id = "p", Kind = WidgetKind.Text, TypeId = Types.Plate, Field = "plate", DeviceId = io.Id, Name = "Last plate", ShowName = false, X = 0, Y = 6, Width = 10, Height = 4, FontSize = 70 },
                    new Widget { Id = "s", Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", DeviceId = gate.Id, Name = "Gate house", Unit = "°C", Decimals = 1, X = 0, Y = 10, Width = 5, Height = 4 },
                    new Widget { Id = "n", Kind = WidgetKind.Number, TypeId = Types.Plate, Field = "speed", DeviceId = gate.Id, Name = "Speed", Unit = "km/h", X = 5, Y = 10, Width = 5, Height = 4 },
                    new Widget { Id = "v", Kind = WidgetKind.Video, DeviceId = camera.Id, Name = camera.Address, X = 10, Y = 0, Width = 10, Height = 10 },
                ]), CancellationToken.None);

                var view = (DashboardsPageView)new DashboardsPage().CreateView(ctx);
                var page = (DashboardsPageViewModel)view.DataContext!;
                var window = Headless.Host(view);
                window.Show();
                Assert.True(await Headless.UntilAsync(() => page.Dashboards.Count == 1 && page.HasEvents));
                Headless.Capture(window, "plugin-dashboards-empty.png");
                page.OpenDashboardCommand.Execute(page.Dashboards[0]);
                Assert.True(await Headless.UntilAsync(() => page.ActiveTab!.Widgets.Where(w => w.Widget.IsValue).All(w => w.Value is not null)));
                var videoWidget = page.ActiveTab!.Widgets.Single(w => w.Widget.Kind == WidgetKind.Video);
                Assert.True(await Headless.UntilAsync(() => ((Oadm.Client.LiveView.LiveVideoViewModel)videoWidget.Video!.View.DataContext!).Image is not null, 30));
                Headless.Pump();
                var widgets = view.GetVisualDescendants().OfType<WidgetView>().ToList();
                Assert.Equal(5, widgets.Count);
                Assert.Contains(widgets, w => w.Display.Note == DashboardTexts.ConnectionLost && w.Display.Dimmed);
                Assert.Contains(widgets, w => w.Display.Caption == "Closed" || w.Display.Caption == "Open");
                Assert.Contains(widgets, w => w.Display.Color == "Red"); // 41.5 reaches the red threshold
                // "Show name" off: the plate shows without its name.
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Last plate" && t.IsEffectivelyVisible);
                Headless.Capture(window, "plugin-dashboards.png");

                // Edit: the cell lines, a frame per widget, the panel of the clicked widget.
                page.EditDashboardCommand.Execute(page.Dashboards[0]);
                page.ActiveTab!.Select(page.ActiveTab.Widgets[0]);
                Headless.Pump();
                Assert.NotNull(page.ActiveTab.Editor);
                Assert.True(view.GetVisualDescendants().OfType<TileFrame>().Count() == 6);
                // One title per widget while editing: the frame's, the widget hides its own name.
                Assert.All(view.GetVisualDescendants().OfType<WidgetView>(), w => Assert.DoesNotContain(
                    w.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Opacity > 0 && t.Classes.Contains("small") && t.Text == "Server room"));
                Headless.Capture(window, "plugin-dashboards-edit.png");
                page.ActiveTab.Select(page.ActiveTab.Widgets[2]);
                Headless.Pump();
                Assert.False(page.ActiveTab.Editor!.ShowName);
                Headless.Capture(window, "plugin-dashboards-text.png");
                page.ActiveTab.Select(page.ActiveTab.Widgets[1]);
                Headless.Pump();
                // The next widget's panel never takes the choices of the one before.
                Assert.Equal(WidgetKind.OnOff, page.ActiveTab.Widgets[1].Widget.Kind);
                Assert.Equal(WidgetKind.Number, page.ActiveTab.Widgets[0].Widget.Kind);
                Assert.True(page.ActiveTab.Widgets[1].IsSelected);
                Assert.False(page.ActiveTab.Widgets[0].IsSelected);
                Headless.Capture(window, "plugin-dashboards-onoff.png");

                // The gate widget copied for three more devices.
                var others = devices.Where(d => !d.HasVideo && d.Id != gate.Id).Take(3).Select(d => d.Id).ToList();
                page.PickDevices = (_, _) => Task.FromResult<IReadOnlyList<Guid>?>(others);
                await page.ActiveTab.Editor!.CopyForDevicesCommand.ExecuteAsync(null);
                Headless.Pump();
                Assert.Equal(6 + others.Count, page.ActiveTab.Widgets.Count);
                Headless.Capture(window, "plugin-dashboards-copies.png");

                // Every widget with a device has the copy icon left of its X.
                var frame = view.GetVisualDescendants().OfType<TileFrame>().First(f => f.Item == page.ActiveTab.Widgets[1]);
                var buttons = frame.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(["Copy for other devices", "Remove widget"], buttons.Select(b => ToolTip.GetTip(b) as string));
                Assert.Contains("danger", buttons[1].Classes); // the X is red

                // Dropping an event shows where it lands.
                var grid = view.ActiveGrid!;
                var free = new Point(grid.Bounds.Width * 0.3, grid.Bounds.Height * 0.85);
                var plate = page.Events.First(e => e.Event?.TypeId == Types.Plate).Event!;
                var preview = DashboardsPageView.PreviewAt(grid, page.ActiveTab, false, free);
                Assert.NotNull(preview);
                grid.ShowPreview(preview, plate.Name);
                Headless.Pump();
                Headless.Capture(window, "plugin-dashboards-drop.png");
                grid.ShowPreview(null);
                Assert.True(DashboardsPageView.DropOn(grid, page.ActiveTab, plate, free));
                Assert.Equal(7 + others.Count, page.ActiveTab.Widgets.Count);
                Assert.False(DashboardsPageView.DropOn(grid, page.ActiveTab, plate, new Point(-5, 5)));
                page.CancelEditCommand.Execute(null);
                Assert.Equal(6, page.ActiveTab.Widgets.Count);
                window.Close();
                page.Dispose();
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
