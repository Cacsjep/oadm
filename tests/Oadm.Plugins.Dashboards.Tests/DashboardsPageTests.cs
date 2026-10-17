using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
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

    [Fact]
    public void A_gauge_shows_the_number_like_the_number_widget_and_where_it_sits()
    {
        var gauge = new Widget { Kind = WidgetKind.Gauge, TypeId = Types.Temperature, Field = "temperature", Unit = "°C", Min = 0, Max = 50, Thresholds = [new(30, "Orange")] };
        var shown = WidgetDisplay.Of(gauge, Snapshot(Types.Temperature, data: ("temperature", "41.5")));
        Assert.Equal((41.5.ToString("N1", CultureInfo.CurrentCulture), "°C", "Orange", (double?)41.5), (shown.Text, shown.Unit, shown.Color, shown.Number));
        var wrong = WidgetDisplay.Of(gauge, Snapshot(Types.Temperature, data: ("temperature", "hot")));
        Assert.Equal(((double?)null, DashboardTexts.NotANumber), (wrong.Number, wrong.Note));
    }

    [Fact]
    public Task Gauges_draw_their_scale_and_follow_the_size() => OnUi(() =>
    {
        WidgetView Gauge(Widget widget, string? value, int width, int height)
        {
            var view = new WidgetView { Width = width, Height = height };
            view.Show(widget, value is null ? WidgetDisplay.Empty : WidgetDisplay.Of(widget, Snapshot(Types.Temperature, data: ("temperature", value))));
            return view;
        }

        var grade = new Widget
        {
            Kind = WidgetKind.Gauge, TypeId = Types.Temperature, Field = "temperature", Name = "Grade", Color = "Accent",
            Thresholds = [new(0, "Red"), new(30, "Amber"), new(50, "Blue"), new(75, "Green")],
        };
        var power = grade with { Name = "Power", Unit = "W", Min = 0, Max = 3000, Thresholds = [new(2500, "Red")], Decimals = 0 };
        var cold = grade with { Name = "Cold room", Unit = "°C", Min = -20, Max = 10, Decimals = 1, Thresholds = [new(-18, "Blue"), new(-5, "Red")] };
        var big = Gauge(grade, "70", 300, 260);
        var watts = Gauge(power, "1250", 300, 260);
        var over = Gauge(grade with { Name = "Out of range" }, "112", 300, 260);
        var none = Gauge(cold, null, 300, 260);
        var tiny = Gauge(grade with { Name = "Small" }, "70", 110, 90);
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { big, watts, over, none, tiny } };
        var window = Headless.Host(new Border { Classes = { "card" }, Padding = new Thickness(12), Child = row });
        window.Show();
        Headless.Pump();

        Assert.Equal(11, big.Gauge.LabelsDrawn); // 0, 10 ... 100
        Assert.Equal(0.7, big.Gauge.MarkerFraction!.Value, 9);
        Assert.Equal(7, watts.Gauge.LabelsDrawn); // 0, 500 ... 3,000
        Assert.Equal(1, over.Gauge.MarkerFraction); // stops at the end, the number stays 112
        Assert.Equal("112", over.Display.Text);
        Assert.Null(none.Gauge.MarkerFraction); // no value yet: the scale only
        Assert.True(none.Gauge.LabelsDrawn > 0);
        Assert.True(tiny.Gauge.LabelsDrawn < 11); // small: labels thin out or go
        Headless.Capture(window, "plugin-dashboards-gauge.png");
        window.Close();
        return Task.CompletedTask;
    });

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
            Assert.Equal(["Number", "Gauge", "Text"], editor.Kinds.Select(k => k.Label));
            Assert.Equal("Automatic (Number)", editor.SelectedType!.Label);
            Assert.True(editor.NeedsDevice);
            Assert.False(vm.SaveDashboardCommand.CanExecute(null) && tab.WidgetList().All(w => w.DeviceId is not null));

            await editor.ChooseDeviceCommand.ExecuteAsync(null);
            Assert.Equal(io.Id, widget.Widget.DeviceId);
            Assert.Equal("Temperature · 10.0.0.120", widget.Widget.Name); // the default name follows the device
            editor.ShowNameChoice = editor.YesNoChoices.Single(c => c.Label == "No"); // the select
            Assert.False(widget.Widget.ShowName);
            editor.ShowNameChoice = null; // a select that lost its list changes nothing
            Assert.False(widget.Widget.ShowName);
            editor.ShowName = true;
            Assert.Equal("Yes", editor.ShowNameChoice!.Label);
            editor.Unit = "°C";
            editor.AddThresholdCommand.Execute(null);
            editor.Thresholds[0].ValueText = "30";
            Assert.Equal([new Threshold(30, "Amber")], widget.Widget.Thresholds);

            // Each new threshold takes a color nobody uses yet (not the widget's either), warning colors first.
            editor.AddThresholdCommand.Execute(null);
            editor.AddThresholdCommand.Execute(null);
            Assert.Equal(["Amber", "Red", "Orange"], editor.Thresholds.Select(t => t.Color.Name));
            editor.Color = editor.Colors.First(c => c.Name == "Green");
            editor.AddThresholdCommand.Execute(null);
            Assert.Equal("Blue", editor.Thresholds[^1].Color.Name); // Green is the widget's own
            while (editor.Thresholds.Count > 1)
            {
                editor.Thresholds[^1].RemoveCommand.Execute(null);
            }

            editor.Color = editor.Colors.First(c => c.Name == "Text");
            Assert.Equal([new Threshold(30, "Amber")], widget.Widget.Thresholds);

            // Gauge: the Number settings stay, Min and Max are its own; a gauge in the text color starts in the accent.
            Assert.Equal("Text", widget.Widget.Color);
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Gauge);
            Assert.Equal((WidgetKind.Gauge, "°C", "Accent"), (widget.Widget.Kind, widget.Widget.Unit, widget.Widget.Color));
            Assert.Equal([new Threshold(30, "Amber")], widget.Widget.Thresholds);
            Assert.True(editor.ShowGauge && editor.ShowNumber && editor.ShowColor);
            Assert.False(editor.ShowText); // a gauge sizes itself
            Assert.Equal("From this value on, the arc and the number take the color.", editor.ThresholdHint);
            Assert.Equal((0d, 100d), (widget.Widget.Min, widget.Widget.Max));
            editor.MaxText = "-5";
            editor.ShowAllErrors();
            Assert.Equal(DashboardRules.RangeWrong, editor.ErrorOf(nameof(WidgetEditorViewModel.MaxText)));
            Assert.Equal(100d, widget.Widget.Max); // the widget keeps the last good range
            editor.MinText = "-40";
            Assert.Null(editor.ErrorOf(nameof(WidgetEditorViewModel.MaxText))); // Max is checked again against the new Min
            Assert.Equal((-40d, -5d), (widget.Widget.Min, widget.Widget.Max));
            editor.MaxText = "60";
            editor.MinText = "abc";
            Assert.Equal(DashboardRules.NotANumber, editor.ErrorOf(nameof(WidgetEditorViewModel.MinText)));
            editor.MinText = "-20";
            Assert.Equal((-20d, 60d), (widget.Widget.Min, widget.Widget.Max));
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Text);
            Assert.Equal(WidgetKind.Text, widget.Widget.Kind);
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Number);

            // The value of the open tab comes at once and then live.
            Assert.True(await Headless.UntilAsync(() => widget.Display.Text is not null));
            Assert.Equal(21.5.ToString("N1", CultureInfo.CurrentCulture), widget.Display.Text);
            host.Feed.Publish(Types.Temperature, io.Id, PublishedEventKind.Fired, ("temperature", "35"));
            host.Tick();
            Assert.True(await Headless.UntilAsync(() => widget.Display.Color == "Amber"));

            // Add cameras adds a live video.
            vm.PickDevices = (_, _, _) => Task.FromResult<IReadOnlyList<Guid>?>([camera.Id]);
            await vm.AddCamerasCommand.ExecuteAsync(null);
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
            vm.PickDevices = (_, _, unavailable) =>
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
            vm.PickDevices = (_, _, _) => Task.FromResult<IReadOnlyList<Guid>?>([.. ports.Select(d => d.Id)]);
            await editor.CopyForDevicesCommand.ExecuteAsync(null);
            Assert.Equal(25, tab.Widgets.Count);
            Assert.Equal(DashboardRules.CopiesAdded(21, 30, DashboardRules.NoRoom), tab.Error);

            // A camera's live video goes to cameras only.
            vm.NewDashboardCommand.Execute(null);
            vm.PickDevices = (_, _, _) => Task.FromResult<IReadOnlyList<Guid>?>([camera.Id]);
            await vm.AddCamerasCommand.ExecuteAsync(null);
            Assert.Equal(DashboardTexts.NoVideo, (await CopyReason(vm, vm.ActiveTab!.Widgets[^1]))(io));
        }
    });

    private static async Task<Func<IDeviceInfo, string?>> CopyReason(DashboardsPageViewModel vm, WidgetViewModel widget)
    {
        Func<IDeviceInfo, string?>? reasons = null;
        vm.PickDevices = (_, _, unavailable) =>
        {
            reasons = unavailable;
            return Task.FromResult<IReadOnlyList<Guid>?>(null);
        };
        await vm.PickCopyDevicesAsync([widget]);
        return reasons!;
    }

    [Fact]
    public Task Several_widgets_are_selected_moved_sized_and_changed_together() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, io) = Page();
        var ports = Enumerable.Range(1, 3).Select(i => (IDeviceInfo)new TestDevice("10.0.1." + i, "A9210") { Category = DeviceCategory.IoModule }).ToList();
        ctx.Items.AddRange(ports);
        ctx.RaiseDevicesChanged();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            var gate = vm.Events.First(e => e.Event?.TypeId == Types.Gate).Event!;
            var temperature = vm.Events.First(e => e.Event?.TypeId == Types.Temperature).Event!;
            Assert.True(tab.DropEvent(gate, 0, 0));
            Assert.True(tab.DropEvent(gate, 4, 0));
            Assert.True(tab.DropEvent(temperature, 12, 0));
            var (a, b, c) = (tab.Widgets[0], tab.Widgets[1], tab.Widgets[2]);
            a.Update(a.Widget with { DeviceId = ports[0].Id, OnText = "Open" });
            b.Update(b.Widget with { DeviceId = ports[1].Id, OnText = "Auf" });
            c.Update(c.Widget with { DeviceId = io.Id });

            // Ctrl+click: two selected, the group panel shows what they share.
            tab.Select(a);
            tab.Toggle(b);
            Assert.Equal([a, b], tab.Selection);
            Assert.Null(tab.Editor);
            var group = tab.GroupEditor!;
            Assert.Equal("2 widgets selected", group.Title);
            Assert.True(group.ShowOnOff); // both On / Off
            Assert.Equal(string.Empty, group.OnText); // they differ: "Mixed"
            Assert.Equal(GroupEditorViewModel.MixedText, group.OnTextWatermark);
            Assert.Equal("status.ok", group.OnIcon!.Key); // the same: shown
            Assert.Equal("Yes", group.ShowNameChoice!.Label);
            Assert.True(a.IsSelected && b.IsSelected && !c.IsSelected);

            // A change goes to both, nothing else of them changes.
            group.OnColor = WidgetEditorViewModel.ColorOptions.First(o => o.Name == "Red");
            group.OnText = "On";
            Assert.All([a, b], w => Assert.Equal(("Red", "On"), (w.Widget.OnColor, w.Widget.OnText)));
            Assert.NotEqual(a.Widget.DeviceId, b.Widget.DeviceId);

            // Moving one selected widget moves the group; it stops at the edge and at other widgets.
            Assert.True(((Oadm.Sdk.Client.Controls.ITileGridSource)tab).TryMove(a, a.Rect with { Y = 6 }));
            Assert.Equal((6, 6), (a.Rect.Y, b.Rect.Y));
            Assert.Equal(0, c.Rect.Y); // not selected: stays
            Assert.False(tab.TryMoveSelection(-5, 0)); // at the left edge already: nothing moves
            Assert.True(tab.TryMoveSelection(2, 0));
            Assert.Equal((2, 6), (a.Rect.X, b.Rect.X));
            Assert.False(tab.TryMoveSelection(12, -6)); // onto the temperature widget

            // Width / Height: every widget that has room.
            group.WidthText = "3";
            Assert.All([a, b], w => Assert.Equal(3, w.Rect.Width));
            group.HeightText = "25";
            Assert.NotNull(group.GetErrors(nameof(GroupEditorViewModel.HeightText)).Cast<object>().FirstOrDefault()); // 2 to 20
            Assert.All([a, b], w => Assert.Equal(4, w.Rect.Height));

            // The rectangle selects every widget it touches; Ctrl+A all; a click on empty cells none.
            tab.SelectArea(new Oadm.Sdk.Layout.TileRect(12, 0, 2, 2), add: false);
            Assert.Equal([c], tab.Selection);
            Assert.NotNull(tab.Editor); // one: its own panel
            tab.SelectArea(new Oadm.Sdk.Layout.TileRect(2, 6, 1, 1), add: true);
            Assert.Equal(2, tab.Selection.Count);
            Assert.False(tab.GroupEditor!.ShowOnOff); // On / Off and Number: no On / Off settings
            Assert.True(tab.GroupEditor.ShowValueSettings);
            tab.SelectAll();
            Assert.Equal(3, tab.Selection.Count);
            tab.Select(null);
            Assert.Empty(tab.Selection);
            Assert.Null(tab.Panel);

            // Copy the two gate widgets for one more I/O module: a copy of each.
            tab.Select(a);
            tab.Toggle(b);
            vm.PickDevices = (_, _, _) => Task.FromResult<IReadOnlyList<Guid>?>([ports[2].Id]);
            await tab.GroupEditor!.CopyForDevicesCommand.ExecuteAsync(null);
            Assert.Equal(5, tab.Widgets.Count);
            Assert.All(tab.Widgets.Skip(3), w => Assert.Equal(ports[2].Id, w.Widget.DeviceId));

            // Remove the group.
            tab.Select(a);
            tab.Toggle(b);
            tab.GroupEditor!.RemoveAllCommand.Execute(null);
            Assert.Equal(3, tab.Widgets.Count);
            Assert.Empty(tab.Selection);

            // Outside editing nothing is selected.
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            tab.Select(tab.Widgets[0]);
            Assert.Empty(tab.Selection);
        }
    });

    [Fact]
    public Task Dragging_an_edge_resizes_every_selected_widget() => OnUi(async () =>
    {
        var (host, _, vm, _, _) = Page();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            var gate = vm.Events.First(e => e.Event?.TypeId == Types.Gate).Event!;
            Assert.True(tab.DropEvent(gate, 0, 0)); // a row of three 4 x 4: 0, 4, 8
            Assert.True(tab.DropEvent(gate, 4, 0));
            Assert.True(tab.DropEvent(gate, 8, 0));
            Assert.True(tab.DropEvent(gate, 0, 10)); // not selected, below
            var (a, b, c) = (tab.Widgets[0], tab.Widgets[1], tab.Widgets[2]);
            tab.Select(a);
            tab.Toggle(b);
            tab.Toggle(c);
            var grid = (Oadm.Sdk.Client.Controls.ITileGridSource)tab;
            static (int, int, int, int) R(WidgetViewModel w) => (w.Rect.X, w.Rect.Y, w.Rect.Width, w.Rect.Height);

            // The right edge of the middle one, 2 cells: all grow, the ones to the right make room.
            Assert.True(grid.TryMove(b, b.Rect with { Width = 6 }));
            Assert.Equal([(0, 0, 6, 4), (6, 0, 6, 4), (12, 0, 6, 4)], new[] { a, b, c }.Select(R));

            // The bottom edge: all get taller (side by side, nothing to push).
            Assert.True(grid.TryMove(a, a.Rect with { Height = 6 }));
            Assert.All([a, b, c], w => Assert.Equal(6, w.Rect.Height));

            // Too tall: the unselected widget below is in the way, nothing changes.
            Assert.False(grid.TryMove(a, a.Rect with { Height = 12 }));
            Assert.All([a, b, c], w => Assert.Equal(6, w.Rect.Height));

            // Past the grid's right edge: nothing changes.
            Assert.False(grid.TryMove(c, c.Rect with { Width = 8 }));

            // Smaller again with the left edge of the last one: anchored at the right, all shrink.
            Assert.True(grid.TryMove(c, c.Rect with { X = c.Rect.X + 2, Width = c.Rect.Width - 2 }));
            Assert.Equal([(6, 0, 4, 6), (10, 0, 4, 6), (14, 0, 4, 6)], new[] { a, b, c }.Select(R));

            // Not below 2 x 2.
            Assert.False(tab.TryResizeSelection(0, -3, 0, 0));
        }
    });

    [Fact]
    public Task Add_cameras_adds_live_videos_up_to_16() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, io) = Page();
        var cameras = Enumerable.Range(1, 20).Select(i => (IDeviceInfo)new TestDevice("10.0.2." + i, "P3265-V")).ToList();
        ctx.Items.AddRange(cameras);
        ctx.RaiseDevicesChanged();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded));
            Assert.False(vm.AddCamerasCommand.CanExecute(null)); // only while editing
            vm.NewDashboardCommand.Execute(null);
            Assert.True(vm.AddCamerasCommand.CanExecute(null));

            IReadOnlyList<IDeviceInfo>? offered = null;
            Func<IDeviceInfo, string?>? reasons = null;
            string? heading = null;
            vm.PickDevices = (title, devices, unavailable) =>
            {
                (heading, offered, reasons) = (title, devices, unavailable);
                return Task.FromResult<IReadOnlyList<Guid>?>([camera.Id]);
            };
            await vm.AddCamerasCommand.ExecuteAsync(null);
            Assert.Equal("Add cameras", heading);
            Assert.DoesNotContain(offered!, d => d.Id == io.Id); // cameras only
            Assert.Single(vm.ActiveTab!.Widgets);

            vm.PickDevices = (_, _, unavailable) =>
            {
                reasons = unavailable;
                return Task.FromResult<IReadOnlyList<Guid>?>([.. cameras.Select(d => d.Id)]);
            };
            await vm.AddCamerasCommand.ExecuteAsync(null);
            Assert.Equal(DashboardTexts.OnDashboard, reasons!(camera)); // shown already
            Assert.Equal(16, vm.ActiveTab.Widgets.Count(w => w.Widget.Kind == WidgetKind.Video));
            Assert.Equal(DashboardRules.CamerasAdded(15, 20, DashboardRules.TooManyVideos), vm.ActiveTab.Error);
            Assert.Null(DashboardRules.WidgetsProblem(vm.ActiveTab.WidgetList()));
        }
    });

    [Fact]
    public Task The_device_pickers_offer_only_the_devices_of_the_event() => OnUi(async () =>
    {
        var (host, ctx, vm, camera, io) = Page();
        var other = new TestDevice("10.0.0.12", "P12-MkIII");
        ctx.Items.Add(other);
        Types.GateDevices.Clear();
        Types.GateDevices.AddRange([io.Id, camera.Id]);
        host.Feed.ChangeTypes(types =>
        {
            types.Clear();
            types.AddRange(Types.All());
        });
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.ApplyCatalog(host.Plugin.Catalog());
            IReadOnlyList<IDeviceInfo>? offered = null;
            vm.PickDevice = (devices, _) =>
            {
                offered = devices;
                return Task.FromResult<Guid?>(io.Id);
            };
            vm.NewDashboardCommand.Execute(null);
            vm.ClickEvent(vm.Events.First(e => e.Event?.TypeId == Types.Gate).Event!);
            await vm.ActiveTab!.Editor!.ChooseDeviceCommand.ExecuteAsync(null);
            Assert.Equal(new HashSet<Guid> { io.Id, camera.Id }, offered!.Select(d => d.Id).ToHashSet()); // the trigger's devices only

            // An event of any device (no list) offers every device.
            vm.ClickEvent(vm.Events.First(e => e.Event?.TypeId == Types.Plate).Event!);
            await vm.ActiveTab.Editor!.ChooseDeviceCommand.ExecuteAsync(null);
            Assert.Contains(offered!, d => d.Id == other.Id);
        }

        Types.GateDevices.Clear();
    });

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
                page.PickDevices = (_, _, _) => Task.FromResult<IReadOnlyList<Guid>?>(others);
                await page.ActiveTab.Editor!.CopyForDevicesCommand.ExecuteAsync(null);
                Headless.Pump();
                Assert.Equal(6 + others.Count, page.ActiveTab.Widgets.Count);
                Headless.Capture(window, "plugin-dashboards-copies.png");

                // Real clicks: a click selects one widget, Ctrl+click adds another.
                Point CenterOf(WidgetViewModel w)
                {
                    var f = view.GetVisualDescendants().OfType<TileFrame>().First(t => t.Item == w);
                    return f.TranslatePoint(new Point(f.Bounds.Width / 2, f.Bounds.Height / 2), window)!.Value;
                }

                page.ActiveTab.Select(null);
                window.MouseDown(CenterOf(page.ActiveTab.Widgets[0]), MouseButton.Left);
                window.MouseUp(CenterOf(page.ActiveTab.Widgets[0]), MouseButton.Left);
                Headless.Pump();
                Assert.Equal([page.ActiveTab.Widgets[0]], page.ActiveTab.Selection);
                window.MouseDown(CenterOf(page.ActiveTab.Widgets[3]), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(CenterOf(page.ActiveTab.Widgets[3]), MouseButton.Left, RawInputModifiers.Control);
                Headless.Pump();
                Assert.Equal([page.ActiveTab.Widgets[0], page.ActiveTab.Widgets[3]], page.ActiveTab.Selection);
                window.MouseDown(CenterOf(page.ActiveTab.Widgets[3]), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(CenterOf(page.ActiveTab.Widgets[3]), MouseButton.Left, RawInputModifiers.Control);
                Headless.Pump();
                Assert.Equal([page.ActiveTab.Widgets[0]], page.ActiveTab.Selection); // Ctrl+click again: out

                // Several selected: the group panel.
                page.ActiveTab.Select(page.ActiveTab.Widgets[1]);
                foreach (var copy in page.ActiveTab.Widgets.Skip(6))
                {
                    page.ActiveTab.Toggle(copy);
                }

                Headless.Pump();
                Assert.NotNull(page.ActiveTab.GroupEditor);
                Assert.IsType<GroupEditorView>(view.GetVisualDescendants().OfType<GroupEditorView>().Single());
                Headless.Capture(window, "plugin-dashboards-group.png");

                // The selection rectangle while dragging over empty cells.
                view.ActiveGrid!.ShowSelectionArea(new Oadm.Sdk.Layout.TileRect(0, 10, 12, 5));
                Headless.Pump();
                Headless.Capture(window, "plugin-dashboards-area.png");
                view.ActiveGrid.ShowSelectionArea(null);
                page.ActiveTab.Select(page.ActiveTab.Widgets[1]);

                // Every widget with a device has the copy icon left of its X.
                var frame = view.GetVisualDescendants().OfType<TileFrame>().First(f => f.Item == page.ActiveTab.Widgets[1]);
                var buttons = frame.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(["Copy for other devices", "Remove widget"], buttons.Select(b => ToolTip.GetTip(b) as string));
                Assert.Contains("danger", buttons[1].Classes); // the X is red

                // Dropping an event shows where it lands.
                var grid = view.ActiveGrid!;
                var free = new Point(grid.Bounds.Width * 0.3, grid.Bounds.Height * 0.85);
                var plate = page.Events.First(e => e.Event?.TypeId == Types.Plate).Event!;
                var preview = DashboardsPageView.PreviewAt(grid, page.ActiveTab, free);
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
