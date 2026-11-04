using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Dashboards.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>The page with an event of one fixed device, split by areas: the panel, the widgets, a changed reader, screenshots.</summary>
public sealed partial class DashboardsPageTests
{
    private static void Reader(PluginHost host, Guid device, params string[] areas) =>
        host.Feed.ChangeTypes(types =>
        {
            types.RemoveAll(t => t.Info.Id == Types.TextRead);
            types.Add(Types.Reader(device, areas));
        });

    /// <summary>The server pushes the changed catalog (at most once a second).</summary>
    private static void PushCatalog(PluginHost host) => host.Time.Advance(TimeSpan.FromSeconds(2));

    private static async Task<DashboardTab> NewTabAsync(DashboardsPageViewModel vm)
    {
        Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.FindEvent(Types.TextRead) is not null));
        vm.NewDashboardCommand.Execute(null);
        return vm.ActiveTab!;
    }

    [Fact]
    public Task The_panel_of_a_fixed_event_has_no_device_row_and_asks_for_the_area() => OnUi(async () =>
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        var other = new TestDevice("10.0.0.49", "P3265-V");
        Reader(host, camera.Id, "Gate", "Yard");
        var ctx = new TestContext(host, [camera, other]);
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            var tab = await NewTabAsync(vm);
            Assert.Contains(vm.Events, e => e.Name == "Text recognition / Gate reader");
            vm.EventSearch = "Gate reader";
            Assert.Contains(vm.Events, e => e.Event?.TypeId == Types.TextRead); // found by its reader
            vm.EventSearch = string.Empty;

            Assert.True(tab.AddEvent(vm.FindEvent(Types.TextRead)!));
            var editor = tab.Editor!;
            var widget = tab.Widgets[0];

            // The reader is the choice: no Device row, the widget has the camera by itself, and no copies for other devices.
            Assert.Equal("Text recognition / Gate reader / Text read", editor.EventName);
            Assert.False(editor.ShowDevice);
            Assert.False(editor.NeedsDevice);
            Assert.Null(widget.Widget.DeviceId);
            Assert.Same(camera, widget.Device);
            Assert.False(widget.CanCopy);
            Assert.False(editor.CanCopy);
            Assert.False(widget.CopyForDevicesCommand.CanExecute(null));
            Assert.Equal(0, tab.CopyFor(widget, [other]));

            // The Area select, above the values; the value starts on Text, Area is no value.
            Assert.True(editor.ShowSplit);
            Assert.Equal("Area", editor.SplitLabel);
            Assert.Equal(["Gate", "Yard"], editor.Splits.Select(s => s.Label));
            Assert.Null(editor.SelectedSplit);
            Assert.Equal("text", widget.Widget.Field);
            Assert.DoesNotContain(editor.Fields, f => f.Value.Name == "area");
            Assert.Equal(DashboardRules.ChooseSplit("Area"), widget.Display.Note);
            Assert.Equal("Text · 10.0.0.48", editor.Name);

            // Save asks for it, below the field.
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Equal("Choose an area.", editor.ErrorOf(nameof(WidgetEditorViewModel.SelectedSplit)));
            Assert.Null(tab.Saved);

            editor.SelectedSplit = editor.Splits[1];
            Assert.Equal("Yard", widget.Widget.Split);
            Assert.Null(editor.ErrorOf(nameof(WidgetEditorViewModel.SelectedSplit)));
            Assert.Equal("Text · Yard", editor.Name);
            Assert.Equal(new ValueKey(Types.TextRead, camera.Id, "Yard"), widget.Key);
            Assert.Equal(DashboardTexts.NoValue, widget.Display.Note);

            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Null(tab.Error);
            Assert.NotNull(tab.Saved);
            Assert.Equal("Yard", tab.Saved!.Widgets[0].Split);
        }
    });

    [Fact]
    public Task A_single_area_is_preselected() => OnUi(async () =>
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        Reader(host, camera.Id, "Gate");
        var ctx = new TestContext(host, [camera]);
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            var tab = await NewTabAsync(vm);
            Assert.True(tab.AddEvent(vm.FindEvent(Types.TextRead)!));

            Assert.Equal("Gate", tab.Editor!.SelectedSplit!.Value);
            Assert.Equal("Gate", tab.Widgets[0].Widget.Split);
            Assert.Equal("Text · Gate", tab.Widgets[0].Widget.Name);
            Assert.Equal(new ValueKey(Types.TextRead, camera.Id, "Gate"), tab.Widgets[0].Key);
        }
    });

    [Fact]
    public Task A_widget_from_before_areas_counted_asks_for_one_and_a_single_area_needs_no_change() => OnUi(async () =>
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        Reader(host, camera.Id, "Gate", "Yard");
        var old = new Widget { Id = "old", Kind = WidgetKind.Text, TypeId = Types.TextRead, Field = "text", DeviceId = camera.Id, Name = "Container" };
        await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Old", 0, [old]), CancellationToken.None);
        var ctx = new TestContext(host, [camera]);
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.Dashboards.Count == 1 && vm.FindEvent(Types.TextRead) is not null));
            vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
            var widget = vm.ActiveTab!.Widgets[0];
            Assert.Equal(DashboardRules.ChooseSplit("Area"), widget.Display.Note);
            Assert.Null(widget.Key);

            // Saving it without an area is refused like a widget without a device.
            vm.EditDashboardCommand.Execute(vm.Dashboards[0]);
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Equal("Choose an area for every widget.", vm.ActiveTab.Error);
            vm.CancelEditCommand.Execute(null);

            // One area left: the widget shows it, nothing to change.
            Reader(host, camera.Id, "Gate");
            PushCatalog(host);
            Assert.True(await Headless.UntilAsync(() => vm.ActiveTab.Widgets[0].Key == new ValueKey(Types.TextRead, camera.Id, "Gate")));
        }
    });

    [Fact]
    public Task A_widget_follows_the_readers_camera_and_says_when_its_area_is_gone() => OnUi(async () =>
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        var next = new TestDevice("10.0.0.49", "P3265-V");
        Reader(host, camera.Id, "Gate", "Yard");
        var ctx = new TestContext(host, [camera, next]);
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Yard", 0,
            [
                new Widget { Id = "v", Kind = WidgetKind.Text, TypeId = Types.TextRead, Field = "text", Split = "Gate", Name = "Gate" },
                new Widget { Id = "t", Kind = WidgetKind.Table, TypeId = Types.TextRead, Field = "text", Split = "Gate", Name = "Log", X = 4 },
            ]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.Dashboards.Count == 1 && vm.FindEvent(Types.TextRead) is not null));
            vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
            var widget = vm.ActiveTab!.Widgets.Single(w => w.Id == "v");
            var table = vm.ActiveTab.Widgets.Single(w => w.Id == "t");
            Assert.True(await Headless.UntilAsync(() => host.Plugin.WatchedCount == 1));
            Assert.Same(camera, widget.Device);

            // The reader's camera changes: the widget and the table watch the new one, nothing was saved.
            Reader(host, next.Id, "Gate", "Yard");
            PushCatalog(host);
            Assert.True(await Headless.UntilAsync(() => widget.Key == new ValueKey(Types.TextRead, next.Id, "Gate") && table.Table?.Key.DeviceId == next.Id));
            Assert.Same(next, widget.Device);
            Assert.Equal(1L, saved.Version);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => host.Plugin.Tables.Read(new TableRead("t", table.Table!.Key, null, 0)) is not null, "the table moved with the reader");
            Assert.True(await Headless.UntilAsync(() => host.Plugin.WatchedCount == 1));
            host.Feed.Publish(Types.TextRead, next.Id, PublishedEventKind.Fired, ("area", "Gate"), ("text", "on the new camera"));
            host.Tick();
            Assert.True(await Headless.UntilAsync(() => widget.Display.Text == "on the new camera" && table.Table!.Count == 1));

            // The area is renamed: one plain line, the search goes, and the panel offers the areas there are.
            Reader(host, next.Id, "Dock", "Yard");
            PushCatalog(host);
            Assert.True(await Headless.UntilAsync(() => widget.Display.Note == DashboardRules.SplitGone("Area")));
            Assert.Equal("Area no longer exists", widget.Display.Note);
            Assert.False(widget.Display.HasValue);
            Assert.Null(widget.Key);
            Assert.Null(table.Table);
            Assert.Equal("Area no longer exists", table.Display.Note);

            vm.EditDashboardCommand.Execute(vm.Dashboards[0]);
            var tab = vm.ActiveTab;
            tab.Select(widget);
            var editor = tab.Editor!;
            Assert.Equal(DashboardRules.SplitGone("Area"), editor.SplitHint);
            Assert.Null(editor.SelectedSplit);
            Assert.Equal("Gate", widget.Widget.Split); // kept until the user chooses

            editor.SelectedSplit = editor.Splits.First(s => s.Value == "Dock");
            Assert.Null(editor.SplitHint);
            Assert.Equal("Dock", widget.Widget.Split);
            Assert.Equal(new ValueKey(Types.TextRead, next.Id, "Dock"), widget.Key);
            Assert.Equal(DashboardTexts.NoValue, widget.Display.Note);

            // A choice that is gone never stops a save: the table keeps saying so.
            tab.Select(null);
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Null(tab.Error);
            Assert.False(tab.IsEditing);
        }
    });

    [Fact]
    public Task Two_widgets_on_different_areas_show_their_own_values_and_the_panel_shows_the_area() => OnUi(async () =>
    {
        var host = new PluginHost();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        Reader(host, camera.Id, "Gate", "Yard");
        var ctx = new TestContext(host, [camera]);
        using (host)
        {
            await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Container yard", 0,
            [
                new Widget { Id = "gate", Kind = WidgetKind.Text, TypeId = Types.TextRead, Field = "text", Split = "Gate", Name = "Text · Gate", X = 0, Y = 0, Width = 8, Height = 4 },
                new Widget { Id = "yard", Kind = WidgetKind.Text, TypeId = Types.TextRead, Field = "text", Split = "Yard", Name = "Text · Yard", X = 8, Y = 0, Width = 8, Height = 4 },
                new Widget { Id = "conf", Kind = WidgetKind.Number, TypeId = Types.TextRead, Field = "confidence", Split = "Yard", Name = "Confidence · Yard", Unit = "%", X = 8, Y = 4, Width = 4, Height = 4 },
                new Widget { Id = "log", Kind = WidgetKind.Table, TypeId = Types.TextRead, Field = "text", Split = "Gate", Name = "Log · Gate", X = 0, Y = 4, Width = 8, Height = 8 },
            ]), CancellationToken.None);
            host.Feed.Publish(Types.TextRead, camera.Id, PublishedEventKind.Fired, ("area", "Gate"), ("text", "AAAU 123456 7"), ("confidence", "98.5"));
            host.Feed.Publish(Types.TextRead, camera.Id, PublishedEventKind.Fired, ("area", "Yard"), ("text", "MSKU 765432 1"), ("confidence", "91.2"));

            var view = (DashboardsPageView)new DashboardsPage().CreateView(ctx);
            var page = (DashboardsPageViewModel)view.DataContext!;
            var window = Headless.Host(view);
            window.Show();
            Assert.True(await Headless.UntilAsync(() => page.Dashboards.Count == 1 && page.FindEvent(Types.TextRead) is not null));
            page.OpenDashboardCommand.Execute(page.Dashboards[0]);
            Assert.True(await Headless.UntilAsync(() => page.ActiveTab!.Widgets.Where(w => w.Id is "gate" or "yard").All(w => w.Display.HasValue)));
            var tab = page.ActiveTab!;
            Assert.Equal("AAAU 123456 7", tab.Widgets.Single(w => w.Id == "gate").Display.Text);
            Assert.Equal("MSKU 765432 1", tab.Widgets.Single(w => w.Id == "yard").Display.Text);
            Assert.True(await Headless.UntilAsync(() => tab.Widgets.Single(w => w.Id == "log").Table!.Count == 1));

            // A new reading of one area changes only that widget.
            host.Feed.Publish(Types.TextRead, camera.Id, PublishedEventKind.Fired, ("area", "Yard"), ("text", "CMAU 111111 1"));
            host.Tick();
            Assert.True(await Headless.UntilAsync(() => tab.Widgets.Single(w => w.Id == "yard").Display.Text == "CMAU 111111 1"));
            Assert.Equal("AAAU 123456 7", tab.Widgets.Single(w => w.Id == "gate").Display.Text);
            Assert.Equal(1, tab.Widgets.Single(w => w.Id == "log").Table!.Count);
            Headless.Pump();
            Headless.Capture(window, "plugin-dashboards-areas.png");

            page.EditDashboardCommand.Execute(page.Dashboards[0]);
            tab.Select(tab.Widgets.Single(w => w.Id == "yard"));
            Headless.Pump();
            var labels = view.GetVisualDescendants().OfType<FormField>().Where(f => f.IsEffectivelyVisible).Select(f => f.Label).ToList();
            Assert.Contains("Area", labels);
            Assert.DoesNotContain("Device", labels);
            Assert.True(labels.IndexOf("Area") < labels.IndexOf("Value"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TileFrame>().SelectMany(f => f.GetVisualDescendants().OfType<Button>()), b => ToolTip.GetTip(b) as string == "Copy for other devices" && b.IsVisible);
            Headless.Capture(window, "plugin-dashboards-area-panel.png");

            // A widget without an area: the panel's field and the widget ask.
            page.CancelEditCommand.Execute(null);
            Assert.True(await Headless.UntilAsync(() => tab.Widgets.Count == 4));
            page.EditDashboardCommand.Execute(page.Dashboards[0]);
            Assert.True(tab.AddEvent(page.FindEvent(Types.TextRead)!));
            Headless.Pump();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == DashboardRules.ChooseSplit("Area") && t.IsEffectivelyVisible);
            Headless.Capture(window, "plugin-dashboards-area-choose.png");

            page.Dispose();
            window.Close();
        }
    });
}
