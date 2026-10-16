using System.Diagnostics;
using System.Text.Json;

using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>The server part: stored dashboards and their rules, the values in memory, old values, watching and the catalog.</summary>
public sealed class DashboardsServerTests(ITestOutputHelper output)
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static Widget Value(string id, int x, int y, string typeId = Types.Temperature, string field = "temperature", Guid? device = null) => new()
    {
        Id = id,
        Kind = WidgetKind.Number,
        X = x,
        Y = y,
        TypeId = typeId,
        Field = field,
        DeviceId = device ?? A,
        Name = "Temperature",
    };

    private static Widget Video(string id, int x, int y, Guid device) => new() { Id = id, Kind = WidgetKind.Video, X = x, Y = y, DeviceId = device };

    private static SaveDashboardRequest New(string name, params Widget[] widgets) => new(null, name, 0, widgets);

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json!, DashboardsPluginInfo.Json)!;

    // ------------------------------------------------------------------ dashboards

    [Fact]
    public async Task Dashboards_are_stored_sorted_by_name_versioned_and_announced()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(New("Hall", Value("a", 0, 0)), CancellationToken.None);
        await host.Plugin.SaveAsync(New("entrance"), CancellationToken.None);
        var changed = await host.Plugin.SaveAsync(new SaveDashboardRequest(hall.Id, "Hall", hall.Version, [Value("a", 4, 4), Video("v", 10, 10, B)]), CancellationToken.None);

        var list = Read<DashboardList>(await host.Plugin.InvokeAsync(DashboardsMethods.List, null, CancellationToken.None));
        Assert.Equal(["entrance", "Hall"], list.Dashboards.Select(d => d.Name));
        Assert.Equal(2, changed.Version);
        Assert.Equal((4, 4), (list.Dashboards[1].Widgets[0].X, list.Dashboards[1].Widgets[0].Y));
        Assert.Equal(WidgetKind.Video, list.Dashboards[1].Widgets[1].Kind);
        Assert.Equal(3, host.Events.Of(DashboardsMethods.DashboardsTopic).Count);
        Assert.Contains("\"kind\":\"Number\"", host.Settings.Values["dashboards"], StringComparison.Ordinal); // readable JSON

        // Dashboards saved before "Show name" existed show their names.
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Widget>("""{"id":"a","kind":"Text"}""", DashboardsPluginInfo.Json)!.ShowName);

        // A restart reads them back.
        using var again = new PluginHost();
        again.Settings.Values["dashboards"] = host.Settings.Values["dashboards"];
        Assert.Equal(2, (await again.Plugin.ListAsync(CancellationToken.None)).Dashboards.Count);
    }

    [Fact]
    public async Task An_older_version_a_taken_name_and_bad_widgets_are_refused()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(New("Hall"), CancellationToken.None);
        await host.Plugin.SaveAsync(new SaveDashboardRequest(hall.Id, "Hall", hall.Version, [Value("a", 0, 0)]), CancellationToken.None);

        async Task<string> Refused(SaveDashboardRequest request) =>
            (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(request, CancellationToken.None))).Message;

        Assert.Equal(DashboardRules.ChangedMeanwhile("Hall"), await Refused(new SaveDashboardRequest(hall.Id, "Hall", hall.Version, [])));
        Assert.Equal(DashboardRules.NameTaken("hall"), await Refused(New("hall")));
        Assert.Equal(DashboardRules.NameMissing, await Refused(New(" ")));
        Assert.Equal(DashboardRules.Overlap, await Refused(New("X", Value("a", 0, 0), Value("b", 2, 2))));
        Assert.Equal(DashboardRules.Outside, await Refused(New("X", Value("a", 18, 18) with { Width = 4 })));
        Assert.Equal(DashboardRules.Outside, await Refused(New("X", Value("a", 0, 0) with { Width = 1 })));
        Assert.Equal(DashboardRules.NoField, await Refused(New("X", Value("a", 0, 0) with { Field = null })));
        Assert.Equal(DashboardRules.NoCamera, await Refused(New("X", Video("v", 0, 0, Guid.Empty))));
        Assert.Equal(DashboardRules.UnitTooLong, await Refused(New("X", Value("a", 0, 0) with { Unit = "degrees Celsius" })));
        Assert.Equal(DashboardRules.BadSetting, await Refused(New("X", Value("a", 0, 0) with { Color = "#ff0000" })));
        Assert.Equal(DashboardRules.BadSetting, await Refused(New("X", Value("a", 0, 0) with { OnIcon = "rocket" })));
        Assert.Equal(DashboardRules.BadSetting, await Refused(New("X", Value("a", 0, 0) with { FontSize = 5 })));
        Assert.Equal(DashboardRules.BadSetting, await Refused(New("X", Value("a", 0, 0), Value("a", 4, 0))));
        Assert.Equal(DashboardRules.TooManyThresholds, await Refused(New("X", Value("a", 0, 0) with { Thresholds = [.. Enumerable.Range(0, 11).Select(i => new Threshold(i, "Red"))] })));
        Assert.Equal(DashboardRules.BadSetting, await Refused(New("X", Value("a", 0, 0) with { Thresholds = [new Threshold(double.NaN, "Red")] })));

        // 16 live videos at most, 50 widgets at most.
        var videos = Enumerable.Range(0, 17).Select(i => Video("v" + i, i % 10 * 2, i / 10 * 2, B)).ToArray();
        Assert.Equal(DashboardRules.TooManyVideos, await Refused(New("X", videos)));
        var many = Enumerable.Range(0, 51).Select(i => Value("w" + i, i % 10 * 2, i / 10 * 2) with { Width = 2, Height = 2 }).ToArray();
        Assert.Equal(DashboardRules.TooManyWidgets, await Refused(New("X", many)));
        Assert.NotNull(await host.Plugin.SaveAsync(New("Fifty", many[..50]), CancellationToken.None));

        Assert.Equal(DashboardRules.Gone, await Refused(new SaveDashboardRequest("nope", "X", 1, [])));
        await host.Plugin.DeleteAsync(hall.Id, CancellationToken.None);
        await host.Plugin.DeleteAsync("nope", CancellationToken.None);
        Assert.Equal(["Fifty"], (await host.Plugin.ListAsync(CancellationToken.None)).Dashboards.Select(d => d.Name));
    }

    [Fact]
    public async Task At_most_200_dashboards_and_an_unreadable_list_is_kept()
    {
        using var host = new PluginHost();
        host.Settings.Values["dashboards"] = JsonSerializer.Serialize(
            Enumerable.Range(0, DashboardsPluginInfo.MaxDashboards).Select(i => new Dashboard("d" + i, "D" + i, 1, [])).ToList(), DashboardsPluginInfo.Json);
        Assert.Equal(DashboardRules.TooManyDashboards,
            (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("One more"), CancellationToken.None))).Message);

        using var broken = new PluginHost();
        broken.Settings.Values["dashboards"] = "{not json";
        Assert.Empty((await broken.Plugin.ListAsync(CancellationToken.None)).Dashboards);
        Assert.Equal("{not json", broken.Settings.Values["dashboardsUnreadable"]);
    }

    [Fact]
    public void Showing_needs_the_page_and_changing_needs_Change_dashboards()
    {
        using var host = new PluginHost();
        var plugin = host.Plugin;
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(DashboardsMethods.List));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(DashboardsMethods.Catalog));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(DashboardsMethods.Watch));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(DashboardsMethods.Unwatch));
        Assert.Equal(DashboardsPluginInfo.ManageKey, plugin.RequiredPermission(DashboardsMethods.Save));
        Assert.Equal(DashboardsPluginInfo.ManageKey, plugin.RequiredPermission(DashboardsMethods.Delete));
        Assert.Null(plugin.RequiredPermission("format"));
        Assert.True(plugin.IsAudited(DashboardsMethods.Save));
        Assert.False(plugin.IsAudited(DashboardsMethods.Watch));
        var manage = Assert.Single(plugin.Permissions);
        Assert.Equal(("manage", "Change dashboards", true, false), (manage.Key, manage.Name, manage.OperatorDefault, manage.ViewerDefault));
        Assert.Equal(CorePluginGroup.Monitoring, plugin.Group);
    }

    // ------------------------------------------------------------------ values

    [Fact]
    public void Each_field_keeps_its_last_value_and_an_on_off_event_its_state()
    {
        using var host = new PluginHost();
        host.Feed.Publish(Types.Gate, A, PublishedEventKind.On, ("port", "1"));
        host.Feed.Publish(Types.Plate, A, PublishedEventKind.Fired, ("plate", "W-123"), ("speed", "48"));
        host.Feed.Publish(Types.Plate, A, PublishedEventKind.Fired, ("plate", "W-456"));
        host.Feed.Publish(Types.Gate, A, PublishedEventKind.Off);

        var gate = host.Plugin.Snapshot(new ValueKey(Types.Gate, A));
        Assert.False(gate.On);
        Assert.Equal("1", gate.Data["port"]); // an off event without data keeps the fields
        var plate = host.Plugin.Snapshot(new ValueKey(Types.Plate, A));
        Assert.Equal(("W-456", "48"), (plate.Data["plate"], plate.Data["speed"]));
        Assert.NotNull(plate.Time);

        var none = host.Plugin.Snapshot(new ValueKey(Types.Gate, B));
        Assert.False(none.On); // the server knows every event that is on: none = off
        Assert.Empty(none.Data);
        Assert.Null(none.Time);
    }

    [Fact]
    public void At_most_the_newest_keys_are_kept()
    {
        using var host = new PluginHost(maxKeys: 3);
        var devices = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var device in devices)
        {
            host.Feed.Publish(Types.Temperature, device, PublishedEventKind.Fired, ("temperature", "20"));
        }

        host.Feed.Publish(Types.Temperature, devices[2], PublishedEventKind.Fired, ("temperature", "21")); // updated: kept longest
        host.Feed.Publish(Types.Temperature, Guid.NewGuid(), PublishedEventKind.Fired, ("temperature", "22"));

        Assert.Equal(3, host.Plugin.Values.Count);
        Assert.Equal("21", host.Plugin.Snapshot(new ValueKey(Types.Temperature, devices[2])).Data["temperature"]);
        Assert.Empty(host.Plugin.Snapshot(new ValueKey(Types.Temperature, devices[3])).Data);
        Assert.Equal("20", host.Plugin.Snapshot(new ValueKey(Types.Temperature, devices[4])).Data["temperature"]);
    }

    [Fact]
    public void A_value_says_why_it_may_be_old()
    {
        using var host = new PluginHost();
        var key = new ValueKey(Types.Temperature, A);
        host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", "41.5"));
        Assert.Null(host.Plugin.Snapshot(key).Stale);

        host.Feed.Publish(Types.TemperatureLost, A, PublishedEventKind.On, ("reason", "Timeout"));
        Assert.Equal(DashboardTexts.ConnectionLost, host.Plugin.Snapshot(key).Stale);
        Assert.Null(host.Plugin.Snapshot(new ValueKey(Types.TemperatureLost, A)).Stale); // the lost event itself is fresh
        host.Feed.Publish(Types.TemperatureLost, A, PublishedEventKind.Off);
        Assert.Null(host.Plugin.Snapshot(key).Stale);

        host.Feed.Publish(Types.Unreachable, A, PublishedEventKind.On);
        Assert.Equal("Unreachable", host.Plugin.Snapshot(key).Stale);
        Assert.Null(host.Plugin.Snapshot(new ValueKey(Types.Unreachable, A)).Stale); // OADM's own status is the status
        host.Feed.Publish(Types.Unreachable, A, PublishedEventKind.Off);
        Assert.Null(host.Plugin.Snapshot(key).Stale);

        host.Feed.ChangeTypes(types =>
        {
            var index = types.FindIndex(t => t.Info.Id == Types.Temperature);
            types[index] = types[index] with { Available = false };
            types.RemoveAll(t => t.Info.Id == Types.Plate);
        });
        Assert.Equal(DashboardTexts.SourceOff("Device triggers"), host.Plugin.Snapshot(key).Stale);
        Assert.Equal(DashboardTexts.EventGone, host.Plugin.Snapshot(new ValueKey(Types.Plate, A)).Stale);
    }

    [Fact]
    public void Received_values_are_read_and_suggest_a_better_type()
    {
        Assert.True(FieldValues.IsOn("TRUE"));
        Assert.True(FieldValues.IsOn(" on"));
        Assert.True(FieldValues.IsOn("1"));
        Assert.False(FieldValues.IsOn("0"));
        Assert.False(FieldValues.IsOn("open"));
        Assert.True(FieldValues.TryNumber("41.5", out var n) && n == 41.5);
        Assert.False(FieldValues.TryNumber("41,5", out _)); // invariant culture: a comma is no decimal point
        Assert.False(FieldValues.TryNumber("NaN", out _));

        Assert.Equal(EventValueKind.Number, FieldValues.Suggest(EventValueKind.Text, ["20", "21.5", "-3"]));
        Assert.Equal(EventValueKind.Number, FieldValues.Suggest(EventValueKind.Text, ["0", "1", "1"])); // 0 / 1 alone stay Number
        Assert.Equal(EventValueKind.YesNo, FieldValues.Suggest(EventValueKind.Text, ["true", "false", "On"]));
        Assert.Equal(EventValueKind.Text, FieldValues.Suggest(EventValueKind.Text, ["20", "hot"]));
        Assert.Equal(EventValueKind.Text, FieldValues.Suggest(EventValueKind.Text, []));
        Assert.Equal(EventValueKind.YesNo, FieldValues.Suggest(EventValueKind.YesNo, ["7"])); // declared wins

        Assert.Equal("Red", FieldValues.ColorOf(120, [new(50, "Orange"), new(20, "Green"), new(100, "Red")], "Text"));
        Assert.Equal("Orange", FieldValues.ColorOf(50, [new(50, "Orange"), new(20, "Green"), new(100, "Red")], "Text"));
        Assert.Equal("Text", FieldValues.ColorOf(10, [new(50, "Orange"), new(20, "Green")], "Text"));
        Assert.Equal([WidgetKind.OnOff, WidgetKind.Text], FieldValues.WidgetsFor(EventValueKind.YesNo));
        Assert.Equal([WidgetKind.Number, WidgetKind.Text], FieldValues.WidgetsFor(EventValueKind.Number));
        Assert.Equal(EventValueKind.Number, FieldValues.Effective(FieldType.Number, EventValueKind.Text));
        Assert.Equal(EventValueKind.YesNo, FieldValues.Effective(FieldType.Automatic, EventValueKind.YesNo));
    }

    // ------------------------------------------------------------------ catalog

    [Fact]
    public void The_catalog_offers_fields_once_they_were_seen()
    {
        using var host = new PluginHost();
        var catalog = host.Plugin.Catalog();
        Assert.Equal(DashboardsPluginInfo.DeviceTriggersId, catalog.Events[0].SourceId); // device triggers first
        var plate = Assert.Single(catalog.Events, e => e.TypeId == Types.Plate);
        Assert.Empty(plate.Fields);
        Assert.True(plate.HasUnseenFields);
        var gate = Assert.Single(catalog.Events, e => e.TypeId == Types.Gate);
        Assert.Equal([DashboardsPluginInfo.ActiveField], gate.Fields.Select(f => f.Name)); // an on / off event: Active at once

        host.Feed.Publish(Types.Plate, A, PublishedEventKind.Fired, ("plate", "W-123"), ("speed", "48"));
        host.Feed.Publish(Types.Gate, A, PublishedEventKind.On, ("port", "3"));
        catalog = host.Plugin.Catalog();
        plate = Assert.Single(catalog.Events, e => e.TypeId == Types.Plate);
        Assert.Equal([("plate", "Plate", EventValueKind.Text), ("speed", "Speed", EventValueKind.Number)], plate.Fields.Select(f => (f.Name, f.Label, f.Kind)));
        Assert.False(plate.HasUnseenFields);
        Assert.Equal(["@active", "port"], Assert.Single(catalog.Events, e => e.TypeId == Types.Gate).Fields.Select(f => f.Name));
        Assert.Contains(catalog.Events, e => e.TypeId == Types.ServerFlag && !e.DeviceScope);

        // A new field is announced, at most once a second.
        host.Tick();
        Assert.Single(host.Events.Of(DashboardsMethods.CatalogTopic));
        host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", "20"));
        host.Tick();
        host.Tick();
        Assert.Single(host.Events.Of(DashboardsMethods.CatalogTopic)); // not yet: the last one is younger than a second
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, host.Events.Of(DashboardsMethods.CatalogTopic).Count);
        Assert.Contains("temperature", host.Events.Of(DashboardsMethods.CatalogTopic)[^1].PayloadJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ watching

    [Fact]
    public async Task Only_watched_values_are_pushed_at_most_4_times_a_second()
    {
        using var host = new PluginHost();
        var watched = new ValueKey(Types.Temperature, A);
        var reply = Read<ValuesReply>(await host.Plugin.InvokeAsync(DashboardsMethods.Watch,
            JsonSerializer.Serialize(new WatchRequest("page1", [watched]), DashboardsPluginInfo.Json), CancellationToken.None));
        Assert.Empty(Assert.Single(reply.Values).Data); // no value yet

        for (var i = 0; i < 10; i++)
        {
            host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", (20 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            host.Feed.Publish(Types.Temperature, B, PublishedEventKind.Fired, ("temperature", "99")); // not watched
        }

        Assert.Empty(host.Events.Of(DashboardsMethods.ValuesTopic)); // nothing before the tick
        host.Tick();
        var pushed = Read<ValuesReply>(Assert.Single(host.Events.Of(DashboardsMethods.ValuesTopic)).PayloadJson);
        var value = Assert.Single(pushed.Values); // ten events, one update with the latest
        Assert.Equal(("29", (Guid?)A), (value.Data["temperature"], value.DeviceId));

        host.Tick(); // nothing changed: nothing sent
        Assert.Single(host.Events.Of(DashboardsMethods.ValuesTopic));

        // The device turned unreachable: its watched values are sent again with the reason.
        host.Feed.Publish(Types.Unreachable, A, PublishedEventKind.On);
        host.Tick();
        var stale = Read<ValuesReply>(host.Events.Of(DashboardsMethods.ValuesTopic)[^1].PayloadJson);
        Assert.Contains(stale.Values, v => v.TypeId == Types.Temperature && v.Stale == "Unreachable");

        await host.Plugin.InvokeAsync(DashboardsMethods.Unwatch, JsonSerializer.Serialize(new UnwatchRequest("page1"), DashboardsPluginInfo.Json), CancellationToken.None);
        Assert.Equal(0, host.Plugin.WatchedCount);
    }

    [Fact]
    public void A_page_that_went_away_is_forgotten_after_30_seconds()
    {
        using var host = new PluginHost();
        host.Plugin.Watch(new WatchRequest("page1", [new ValueKey(Types.Temperature, A)]));
        host.Plugin.Watch(new WatchRequest("page2", [new ValueKey(Types.Gate, A), new ValueKey(Types.Temperature, A)]));
        Assert.Equal(2, host.Plugin.WatchedCount);

        host.Time.Advance(TimeSpan.FromSeconds(20));
        host.Plugin.Watch(new WatchRequest("page2", [new ValueKey(Types.Gate, A)])); // renewed, one tab closed
        host.Time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(1, host.Plugin.WatchedCount); // page1 is gone, page2 watches its one key

        Assert.Throws<ArgumentException>(() => host.Plugin.Watch(new WatchRequest("", [])));
    }

    [Fact]
    public async Task Turned_off_the_plugin_keeps_nothing_and_without_events_it_still_works()
    {
        using var host = new PluginHost();
        host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", "20"));
        Assert.Equal(1, host.Feed.Subscribers);
        await host.Plugin.StopAsync(CancellationToken.None);
        Assert.Equal(0, host.Feed.Subscribers);
        Assert.Equal(0, host.Plugin.Values.Count);

        using var old = new PluginHost(withEvents: false); // an older host: dashboards still save, no values
        Assert.Empty(old.Plugin.Catalog().Events);
        Assert.NotNull(await old.Plugin.SaveAsync(New("Hall"), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Fifty_widgets_updating_4_times_a_second_stay_cheap()
    {
        using var host = new PluginHost();
        var devices = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        host.Plugin.Watch(new WatchRequest("page", [.. devices.Select(d => new ValueKey(Types.Temperature, d))]));
        // 30,000 other devices send too.
        var others = Enumerable.Range(0, 30_000).Select(_ => Guid.NewGuid()).ToList();

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < 40; round++) // 10 s at 4 updates a second
        {
            foreach (var device in devices)
            {
                host.Feed.Publish(Types.Temperature, device, PublishedEventKind.Fired, ("temperature", round.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            for (var i = round * 750; i < (round + 1) * 750; i++)
            {
                host.Feed.Publish(Types.Temperature, others[i], PublishedEventKind.Fired, ("temperature", "20"));
            }

            host.Tick();
        }

        watch.Stop();
        var pushes = host.Events.Of(DashboardsMethods.ValuesTopic);
        output.WriteLine($"{40 * (50 + 750)} events and {pushes.Count} pushes in {watch.ElapsedMilliseconds} ms, {host.Plugin.Values.Count} keys");
        Assert.Equal(40, pushes.Count);
        Assert.All(pushes, p => Assert.Equal(50, Read<ValuesReply>(p.PayloadJson).Values.Count));
        Assert.InRange(watch.ElapsedMilliseconds, 0, 5_000);
    }
}
