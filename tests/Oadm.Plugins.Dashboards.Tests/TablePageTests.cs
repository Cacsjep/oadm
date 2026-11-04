using System.Collections.Specialized;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Dashboards.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>Table widgets on the page: the panel, rows from the server, a burst as one change, the search, the cap, the list stays virtualized, screenshots.</summary>
public sealed partial class DashboardsPageTests
{
    private static readonly string[] Plates =
        ["W-12345A", "B-77812", "Container TCLU 123456 7 at gate 2", "L-4420K", "S-90A71", "G-1203B", "Container left the yard towards the north exit after the second check at gate 7", "W-55120C"];

    private static TableRows Batch(string ring, long from, int count, Func<long, string>? text = null) =>
        new("t", ring, [.. Enumerable.Range(0, count).Select(i => new TableRow(from + i, DateTimeOffset.Parse("2026-10-16T14:02:17Z", CultureInfo.InvariantCulture).AddSeconds(from + i), (text ?? (n => "P" + n))(from + i)))]);

    [Fact]
    public void A_table_keeps_1000_rows_newest_first_and_each_batch_is_one_change()
    {
        var table = new TableWidgetViewModel(new TableKey(Types.Plate, null, "plate"));
        Assert.Equal(DashboardTexts.NoValue, table.EmptyText);
        var changes = 0;
        ((INotifyCollectionChanged)table.Rows).CollectionChanged += (_, _) => changes++;

        table.Apply(Batch("run", 1, 3));
        Assert.Equal(["P3", "P2", "P1"], table.Rows.Select(r => r.Text));
        Assert.Equal(1, changes);
        Assert.Equal(("run", 3L), (table.ReadOf("t").Ring, table.ReadOf("t").After)); // only what is new is asked for next
        Assert.Null(table.EmptyText);

        // Nothing new: no change at all.
        table.Apply(Batch("run", 4, 0));
        Assert.Equal(1, changes);

        // A burst past the cap: one change, the oldest rows gone.
        table.Apply(Batch("run", 4, DashboardsPluginInfo.MaxTableRows + 1));
        Assert.Equal(2, changes);
        Assert.Equal(DashboardsPluginInfo.MaxTableRows, table.Count);
        Assert.Equal(DashboardsPluginInfo.MaxTableRows, table.Rows.Count);
        Assert.Equal("P" + (DashboardsPluginInfo.MaxTableRows + 4), table.Rows[0].Text);
        Assert.Equal("P5", table.Rows[^1].Text);

        // The server started again: its rows replace what the widget has.
        table.Apply(Batch("next", 1, 1));
        Assert.Equal(["P1"], table.Rows.Select(r => r.Text));
    }

    [Fact]
    public void The_search_filters_by_text_and_time_and_says_when_nothing_matches()
    {
        var table = new TableWidgetViewModel(new TableKey(Types.Plate, null, "plate"));
        table.Apply(Batch("run", 1, 3, n => n == 2 ? "W-12345A" : "B-77812"));

        table.Search = " w-123 ";
        Assert.Equal(["W-12345A"], table.Rows.Select(r => r.Text));
        table.Apply(Batch("run", 4, 1, _ => "W-12399"));
        Assert.Equal(["W-12399", "W-12345A"], table.Rows.Select(r => r.Text)); // new rows follow the search
        Assert.Equal(4, table.Count);

        table.Search = table.Rows[0].TimeText;
        Assert.Single(table.Rows);

        table.Search = "nothing like this";
        Assert.Empty(table.Rows);
        Assert.Equal(DashboardTexts.NoMatchingRows, table.EmptyText);
        Assert.True(table.ShowEmpty);

        table.Search = string.Empty;
        Assert.Equal(4, table.Rows.Count);
    }

    [Fact]
    public Task A_value_becomes_a_table_in_the_panel_and_rows_arrive_live() => OnUi(async () =>
    {
        var host = new PluginHost();
        var device = new TestDevice("10.0.0.48", "P3265-V");
        var ctx = new TestContext(host, [device]);
        var reads = 0;
        ctx.Answer = (method, answer) =>
        {
            reads += method == DashboardsMethods.Tables ? 1 : 0;
            return answer;
        };
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.True(tab.AddEvent(vm.FindEvent(Types.Plate)!));
            var widget = tab.Widgets[0];
            widget.Update(widget.Widget with { DeviceId = device.Id });
            var editor = tab.Editor!;
            Assert.Equal([WidgetKind.Text, WidgetKind.Table], editor.Kinds.Select(k => k.Value));
            editor.SelectedKind = editor.Kinds.First(k => k.Value == WidgetKind.Table);
            Assert.Equal("Table", editor.Kinds[1].Label);
            Assert.NotNull(widget.Table);
            Assert.False(editor.ShowText || editor.ShowColor || editor.ShowNumber || editor.ShowOnOff); // no settings of its own

            tab.Select(null);
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Null(tab.Error);
            Assert.True(await Headless.UntilAsync(() => host.Plugin.WatchedCount == 1));
            widget = tab.Widgets[0];

            // A burst of events: every row, one push, each table changed once.
            var before = reads;
            var changes = 0;
            ((INotifyCollectionChanged)widget.Table!.Rows).CollectionChanged += (_, _) => changes++;
            foreach (var plate in Plates[..4])
            {
                host.Feed.Publish(Types.Plate, device.Id, PublishedEventKind.Fired, ("plate", plate));
            }

            host.Tick();
            Assert.True(await Headless.UntilAsync(() => widget.Table.Count == 4));
            Assert.Equal(Plates[..4].Reverse(), widget.Table.Rows.Select(r => r.Text));
            Assert.Equal(1, changes);
            Assert.Equal(1, reads - before);

            // Another value starts empty.
            vm.EditDashboardCommand.Execute(vm.Dashboards[0]);
            var same = tab.Widgets[0].Table;
            tab.Select(tab.Widgets[0]);
            tab.Editor!.SelectedField = tab.Editor.Fields.First(f => f.Value.Name == "speed");
            Assert.NotSame(same, tab.Widgets[0].Table);
            Assert.Equal(0, tab.Widgets[0].Table!.Count);
        }
    });

    [Fact]
    public Task A_new_table_beyond_the_limit_is_not_copied() => OnUi(async () =>
    {
        var host = new PluginHost();
        var devices = Enumerable.Range(1, DashboardsPluginInfo.MaxTables + 2).Select(i => (Oadm.Sdk.Devices.IDeviceInfo)new TestDevice("10.0.0." + i, "M1135")).ToList();
        var ctx = new TestContext(host, devices);
        using (host)
        using (var vm = new DashboardsPageViewModel(ctx))
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded && vm.HasEvents));
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.True(tab.AddEvent(vm.FindEvent(Types.Plate)!));
            var source = tab.Widgets[0];
            source.Update(source.Widget with { DeviceId = devices[0].Id, Kind = WidgetKind.Table, Width = 2, Height = 2 });

            Assert.Equal(DashboardsPluginInfo.MaxTables - 1, tab.CopyFor(source, devices));
            Assert.Equal(DashboardRules.CopiesAdded(DashboardsPluginInfo.MaxTables - 1, devices.Count - 1, DashboardRules.TooManyTables), tab.Error);
        }
    });

    [Fact]
    public Task A_table_widget_keeps_the_list_virtualized_and_shows_rows_and_a_search() => OnUi(async () =>
    {
        var device = new TestDevice("10.0.0.48", "P3265-V");
        var host = new PluginHost();
        var ctx = new TestContext(host, [device]);
        using (host)
        {
            var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate house", 0,
            [
                new Widget { Id = "t", Kind = WidgetKind.Table, TypeId = Types.Plate, Field = "plate", DeviceId = device.Id, Name = "Plates at the gate", X = 0, Y = 0, Width = 9, Height = 12 },
                new Widget { Id = "n", Kind = WidgetKind.Table, TypeId = Types.Plate, Field = "speed", DeviceId = device.Id, Name = "Speed", X = 9, Y = 0, Width = 5, Height = 6 },
                new Widget { Id = "l", Kind = WidgetKind.Text, TypeId = Types.Plate, Field = "plate", DeviceId = device.Id, Name = "Last plate", X = 14, Y = 0, Width = 6, Height = 6 },
            ]), CancellationToken.None);
            for (var i = 0; i < DashboardsPluginInfo.MaxTableRows; i++)
            {
                host.Feed.Publish(Types.Plate, device.Id, PublishedEventKind.Fired, ("plate", Plates[i % Plates.Length]));
            }

            var view = (DashboardsPageView)new DashboardsPage().CreateView(ctx);
            var page = (DashboardsPageViewModel)view.DataContext!;
            var window = Headless.Host(view);
            window.Show();
            Assert.True(await Headless.UntilAsync(() => page.Dashboards.Count == 1));
            page.OpenDashboardCommand.Execute(page.Dashboards[0]);
            Assert.True(await Headless.UntilAsync(() => page.ActiveTab?.Widgets.Count == 3));
            var table = page.ActiveTab!.Widgets.First(w => w.Id == "t").Table!;
            Assert.True(await Headless.UntilAsync(() => table.Count == DashboardsPluginInfo.MaxTableRows));
            Headless.Pump();

            // Only the rows in view exist as controls.
            var widget = view.GetVisualDescendants().OfType<TableWidgetView>().First(t => t.DataContext == table);
            var list = widget.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            var realized = list.GetRealizedContainers().Count();
            Assert.InRange(realized, 1, 100);
            Assert.Single(widget.GetVisualDescendants().OfType<SearchBox>());
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == DashboardTexts.NoValue && t.IsEffectivelyVisible); // the Speed table
            Headless.Capture(window, "plugin-dashboards-table.png");

            table.Search = "W-12345A";
            Headless.Pump();
            Assert.Equal(DashboardsPluginInfo.MaxTableRows / Plates.Length, table.Rows.Count);
            Headless.Capture(window, "plugin-dashboards-table-search.png");

            page.Dispose();
            window.Close();
        }
    });
}
