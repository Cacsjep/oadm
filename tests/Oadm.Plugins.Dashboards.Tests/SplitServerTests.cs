using System.Text.Json;

using Oadm.Sdk.Events;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>
/// Events of one fixed device, split by a choice field (a Text recognition reader with its areas; declared generically by
/// <see cref="EventTypeInfo.FixedDeviceId"/> and <see cref="EventTypeInfo.SplitField"/>): values and table rows per split
/// value, the device read from the event type, older dashboards.
/// </summary>
public sealed class SplitServerTests
{
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid OtherCamera = Guid.NewGuid();

    private static PluginHost Host(int maxKeys = DashboardsPluginInfo.MaxKeys, params string[] areas)
    {
        var host = new PluginHost(maxKeys);
        host.Feed.ChangeTypes(types => types.Add(Types.Reader(Camera, areas.Length > 0 ? areas : ["Gate", "Yard"])));
        return host;
    }

    private static void Read(PluginHost host, string area, string text, Guid? device = null) =>
        host.Feed.Publish(Types.TextRead, device ?? Camera, PublishedEventKind.Fired, ("area", area), ("text", text));

    private static Widget Value(string id, string? area, int x = 0, WidgetKind kind = WidgetKind.Text) => new()
    {
        Id = id,
        Kind = kind,
        X = x,
        TypeId = Types.TextRead,
        Field = "text",
        Split = area,
    };

    private static EventShape Shape(PluginHost host) =>
        Assert.Single(host.Plugin.Catalog().Events, e => e.TypeId == Types.TextRead).Shape;

    private static Task<Dashboard> SaveAsync(PluginHost host, params Widget[] widgets) =>
        host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate", 0, widgets), CancellationToken.None);

    private static async Task<IReadOnlyList<string>> RowsAsync(PluginHost host, TableKey key)
    {
        var reply = await host.Plugin.TablesAsync(new TablesRequest([new TableRead("t", key, null, 0)]), CancellationToken.None);
        return Assert.Single(reply.Tables).Rows.Select(r => r.Text).ToList();
    }

    [Fact]
    public void No_text_read_blanks_only_its_area_and_is_no_event_of_the_catalog()
    {
        const string TextRead = DashboardsPlugin.TextReadTypePrefix + "r1";
        const string NoText = DashboardsPlugin.NoTextTypePrefix + "r1";
        using var host = new PluginHost(DashboardsPluginInfo.MaxKeys);
        host.Feed.ChangeTypes(types =>
        {
            types.Add(Types.Reader(TextRead, "Text read", Camera, "Gate", "Yard"));
            types.Add(Types.Reader(NoText, "No text read", Camera, "Gate", "Yard"));
        });
        host.Feed.Publish(TextRead, Camera, PublishedEventKind.Fired, ("area", "Gate"), ("text", "AAAU 123456 7"));
        host.Feed.Publish(TextRead, Camera, PublishedEventKind.Fired, ("area", "Yard"), ("text", "MSKU 765432 1"));

        host.Feed.Publish(NoText, Camera, PublishedEventKind.Fired, ("area", "Gate"));

        Assert.Equal(string.Empty, host.Plugin.Snapshot(new ValueKey(TextRead, Camera, "Gate")).Data["text"]);
        Assert.Equal("MSKU 765432 1", host.Plugin.Snapshot(new ValueKey(TextRead, Camera, "Yard")).Data["text"]);
        Assert.DoesNotContain(host.Plugin.Catalog().Events, e => e.TypeId == NoText);
    }

    [Fact]
    public void Each_split_value_keeps_its_own_latest_value()
    {
        using var host = Host();
        Read(host, "Gate", "AAAU 123456 7");
        Read(host, "Yard", "MSKU 765432 1");

        Assert.Equal("AAAU 123456 7", host.Plugin.Snapshot(new ValueKey(Types.TextRead, Camera, "Gate")).Data["text"]);
        var yard = host.Plugin.Snapshot(new ValueKey(Types.TextRead, Camera, "Yard"));
        Assert.Equal(("MSKU 765432 1", "Yard"), (yard.Data["text"], yard.Split));
        Assert.Equal(2, host.Plugin.Values.Count);
    }

    [Fact]
    public void An_event_that_declares_no_split_keeps_one_value_per_device()
    {
        using var host = Host();
        host.Feed.Publish(Types.Plate, Camera, PublishedEventKind.Fired, ("plate", "W-1"), ("area", "Gate"));
        host.Feed.Publish(Types.Plate, Camera, PublishedEventKind.Fired, ("plate", "W-2"), ("area", "Yard"));

        Assert.Equal(1, host.Plugin.Values.Count);
        Assert.Equal("W-2", host.Plugin.Snapshot(new ValueKey(Types.Plate, Camera)).Data["plate"]);
    }

    [Fact]
    public void A_field_that_is_no_choice_field_splits_nothing()
    {
        using var host = Host();
        host.Feed.ChangeTypes(types =>
        {
            var plate = types.Single(t => t.Info.Id == Types.Plate);
            types[types.IndexOf(plate)] = plate with { Info = plate.Info with { SplitField = "plate" } };
        });

        Assert.Null(host.Plugin.Catalog().Events.Single(e => e.TypeId == Types.Plate).Shape.Split);
        Assert.Contains(host.Plugin.Catalog().Events.Single(e => e.TypeId == Types.Plate).Fields, f => f.Name == "plate");
    }

    [Fact]
    public void The_limit_counts_split_values_and_the_least_recently_updated_goes_first()
    {
        using var host = Host(maxKeys: 3);
        Read(host, "a", "1");
        Read(host, "b", "2");
        Read(host, "c", "3");
        Read(host, "a", "4"); // updated: kept longest
        Read(host, "d", "5");

        Assert.Equal(3, host.Plugin.Values.Count);
        Assert.Empty(host.Plugin.Snapshot(new ValueKey(Types.TextRead, Camera, "b")).Data);
        Assert.Equal("4", host.Plugin.Snapshot(new ValueKey(Types.TextRead, Camera, "a")).Data["text"]);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void The_limit_of_100000_keys_holds_for_split_values_in_o1_per_event()
    {
        var store = new ValueStore();
        var time = DateTimeOffset.UnixEpoch;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i <= DashboardsPluginInfo.MaxKeys; i++)
        {
            var area = "Area " + i;
            store.Apply(new PublishedEvent(Types.TextRead, Camera, PublishedEventKind.Fired, new Dictionary<string, string> { ["area"] = area }, time), area);
        }

        clock.Stop();
        Assert.Equal(DashboardsPluginInfo.MaxKeys, store.Count);
        Assert.Null(store.Get(new ValueKey(Types.TextRead, Camera, "Area 0"))); // the first is gone
        Assert.NotNull(store.Get(new ValueKey(Types.TextRead, Camera, "Area 1")));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_catalog_says_the_device_the_choices_and_the_folders_and_leaves_out_the_split_field()
    {
        using var host = Host();
        var reader = host.Plugin.Catalog().Events.Single(e => e.TypeId == Types.TextRead);

        Assert.Equal(Camera, reader.Shape.FixedDeviceId);
        Assert.Equal(("area", "Area"), (reader.Shape.Split!.Field, reader.Shape.Split.Label));
        Assert.Equal(["Gate", "Yard"], reader.Shape.Split.Values);
        Assert.Equal("Text recognition / Gate reader", reader.Folder);
        Assert.Equal(["text", "number", "confidence"], reader.Fields.Select(f => f.Name)); // a new widget starts on Text, never on Area

        var other = host.Plugin.Catalog().Events.Single(e => e.TypeId == Types.Plate);
        Assert.Equal(EventShape.None, other.Shape);
        Assert.Equal(other.Group, other.Folder);
    }

    [Fact]
    public async Task A_widget_stores_no_device_of_a_fixed_event_and_follows_the_event()
    {
        using var host = Host();

        // Whatever the page sends, also a widget from before the event declared its device.
        var saved = await SaveAsync(host, Value("v", "Gate") with { DeviceId = OtherCamera }, Value("w", "Yard", 4));
        Assert.All(saved.Widgets, w => Assert.Equal(((Guid?)null, true), (w.DeviceId, w.FixedDevice)));
        Assert.DoesNotContain(OtherCamera.ToString(), host.Settings.Values["dashboards"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new ValueKey(Types.TextRead, Camera, "Gate"), Shape(host).KeyOf(saved.Widgets[0]));

        // The reader moves to another camera: the event type says so, the widget follows.
        host.Feed.ChangeTypes(types =>
        {
            types.RemoveAll(t => t.Info.Id == Types.TextRead);
            types.Add(Types.Reader(OtherCamera, ["Gate", "Yard"]));
        });
        Assert.Equal(new ValueKey(Types.TextRead, OtherCamera, "Gate"), Shape(host).KeyOf(saved.Widgets[0]));
    }

    [Fact]
    public async Task A_table_holds_only_its_split_values_rows_and_shares_them_only_with_the_same_one()
    {
        using var host = Host();
        await SaveAsync(host, Value("gate", "Gate", 0, WidgetKind.Table), Value("gate2", "Gate", 4, WidgetKind.Table), Value("yard", "Yard", 8, WidgetKind.Table));
        Assert.Equal(2, host.Plugin.Tables.Count); // the two Gate tables are one

        Read(host, "Gate", "first");
        Read(host, "Yard", "other");
        Read(host, "Gate", "second");

        var gate = new TableKey(Types.TextRead, Camera, "text", "Gate");
        Assert.Equal(["first", "second"], await RowsAsync(host, gate));
        Assert.Equal(["other"], await RowsAsync(host, gate with { Split = "Yard" }));

        var reads = new TablesRequest([new TableRead("gate", gate, null, 0), new TableRead("gate2", gate, null, 0)]);
        var both = (await host.Plugin.TablesAsync(reads, CancellationToken.None)).Tables;
        Assert.Equal(both[0].Ring, both[1].Ring);
    }

    [Fact]
    public async Task A_table_needs_a_valid_split_value_and_follows_the_event()
    {
        using var host = Host();
        await SaveAsync(host, Value("none", null, 0, WidgetKind.Table), Value("gate", "Gate", 4, WidgetKind.Table));
        Assert.Equal(1, host.Plugin.Tables.Count); // the one without an area records nothing

        // The area is renamed: the table goes with its rows. The reader moves: the table goes with it.
        host.Feed.ChangeTypes(types =>
        {
            types.RemoveAll(t => t.Info.Id == Types.TextRead);
            types.Add(Types.Reader(Camera, ["Dock", "Yard"]));
        });
        await Testing.Eventually.TrueAsync(() => host.Plugin.Tables.Count == 0, "the table of the old area went");

        host.Feed.ChangeTypes(types =>
        {
            types.RemoveAll(t => t.Info.Id == Types.TextRead);
            types.Add(Types.Reader(OtherCamera, ["Gate", "Yard"]));
        });
        await Testing.Eventually.TrueAsync(() => host.Plugin.Tables.Count == 1, "the table is back on the new camera");
        Read(host, "Gate", "moved", OtherCamera);
        Assert.Equal(["moved"], await RowsAsync(host, new TableKey(Types.TextRead, OtherCamera, "text", "Gate")));
    }

    [Fact]
    public async Task A_stored_table_of_a_fixed_event_records_from_the_start_of_the_plugin()
    {
        using var host = Host();
        await SaveAsync(host, Value("gate", "Gate", 0, WidgetKind.Table));

        await host.Plugin.StopAsync(CancellationToken.None);
        await host.Plugin.StartAsync(host.Context, CancellationToken.None);

        Read(host, "Gate", "after a restart");
        Assert.Equal(["after a restart"], await RowsAsync(host, new TableKey(Types.TextRead, Camera, "text", "Gate")));
    }

    [Fact]
    public void A_widget_asks_for_a_split_value_takes_the_only_one_and_says_when_it_is_gone()
    {
        var split = new EventSplit("area", "Area", ["Gate", "Yard"]);
        var shape = new EventShape(Camera, split);

        Assert.Equal("Choose an area.", shape.SplitProblem(Value("v", null)));
        Assert.Null(shape.KeyOf(Value("v", null)));
        Assert.Null(shape.SplitProblem(Value("v", "Gate")));
        Assert.Equal("Area no longer exists", shape.SplitProblem(Value("v", "Dock")));
        Assert.Null(shape.KeyOf(Value("v", "Dock")));

        var single = new EventShape(Camera, split with { Values = ["Gate"] });
        Assert.Equal(new ValueKey(Types.TextRead, Camera, "Gate"), single.KeyOf(Value("v", null))); // a dashboard from before areas counted

        // No split on the event: a stored choice means nothing, the widget works as before.
        Assert.Equal(new ValueKey(Types.TextRead, null), EventShape.None.KeyOf(Value("v", "Gate")));
        Assert.Equal("Choose a zone for every widget.", DashboardRules.ChooseSplitForEvery("Zone"));
    }

    [Fact]
    public void Stored_dashboards_read_as_before_and_a_widget_without_a_split_writes_nothing_new()
    {
        const string Stored = """[{"id":"d","name":"Old","version":3,"widgets":[{"id":"1","kind":"Text","typeId":"t","deviceId":"6f0c2a51-0b8e-4d35-9b5e-0d2b9b3c1a77","field":"f"}]}]""";
        var widget = JsonSerializer.Deserialize<List<Dashboard>>(Stored, DashboardsPluginInfo.Json)![0].Widgets[0];

        Assert.Equal(((string?)null, false), (widget.Split, widget.FixedDevice));
        var written = JsonSerializer.Serialize(widget, DashboardsPluginInfo.Json);
        Assert.DoesNotContain("split", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixedDevice", written, StringComparison.OrdinalIgnoreCase);

        var key = JsonSerializer.Deserialize<ValueKey>("""{"typeId":"t"}""", DashboardsPluginInfo.Json)!;
        Assert.Null(key.Split);
    }

    [Fact]
    public async Task The_server_refuses_a_split_value_it_cannot_store_and_a_button_with_one()
    {
        using var host = Host();
        var tooLong = await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(host, Value("v", new string('x', DashboardRules.MaxIdLength + 1))));
        Assert.Equal(DashboardRules.BadSetting, tooLong.Message);

        var button = new Widget { Id = "b", Kind = WidgetKind.Button, Name = "Open", Split = "Gate" };
        Assert.Equal(DashboardRules.BadSetting, (await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(host, button))).Message);
    }
}
