using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>The rows of table widgets on the server: one per event, the limits, memory only, reads by what is new, device groups.</summary>
public sealed class TableServerTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static Widget Table(string id, int x = 0, string typeId = Types.Plate, string field = "plate", Guid? device = null) => new()
    {
        Id = id,
        Kind = WidgetKind.Table,
        X = x,
        TypeId = typeId,
        Field = field,
        DeviceId = device ?? A,
        Name = "Plates",
    };

    private static Task<Dashboard> SaveAsync(PluginHost host, params Widget[] widgets) =>
        host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate", 0, widgets), CancellationToken.None);

    private static void Plate(PluginHost host, string text, Guid? device = null) =>
        host.Feed.Publish(Types.Plate, device ?? A, PublishedEventKind.Fired, ("plate", text));

    private static async Task<TableRows> ReadAsync(PluginHost host, Widget widget, string? ring = null, long after = 0)
    {
        var reply = await host.Plugin.TablesAsync(new TablesRequest([new TableRead(widget.Id, TableKey.Of(widget)!, ring, after)]), CancellationToken.None);
        return Assert.Single(reply.Tables);
    }

    [Fact]
    public async Task Every_event_adds_a_row_and_past_the_cap_the_oldest_row_goes_first()
    {
        using var host = new PluginHost();
        var table = Table("t");
        await SaveAsync(host, table);

        for (var i = 1; i <= DashboardsPluginInfo.MaxTableRows + 5; i++)
        {
            Plate(host, "P" + i);
        }

        var rows = (await ReadAsync(host, table)).Rows;
        Assert.Equal(1_000, DashboardsPluginInfo.MaxTableRows);
        Assert.Equal(DashboardsPluginInfo.MaxTableRows, rows.Count);
        Assert.Equal("P6", rows[0].Text); // the oldest five are gone
        Assert.Equal("P" + (DashboardsPluginInfo.MaxTableRows + 5), rows[^1].Text);
    }

    [Fact]
    public async Task A_burst_between_two_pushes_keeps_every_row_and_a_read_brings_only_what_is_new()
    {
        using var host = new PluginHost();
        var table = Table("t");
        await SaveAsync(host, table);
        host.Plugin.Watch(new WatchRequest("page", [new ValueKey(Types.Plate, A)]));

        Plate(host, "first");
        var first = await ReadAsync(host, table);
        Assert.Equal(["first"], first.Rows.Select(r => r.Text));

        foreach (var text in new[] { "a", "b", "c" })
        {
            Plate(host, text);
        }

        host.Tick();
        await Testing.Eventually.TrueAsync(() => host.Events.Of(DashboardsMethods.ValuesTopic).Count == 1, "one push with the latest value");
        var next = await ReadAsync(host, table, first.Ring, first.Rows[^1].Seq);
        Assert.Equal(["a", "b", "c"], next.Rows.Select(r => r.Text)); // the value keeps only "c", the table all three
        Assert.Empty((await ReadAsync(host, table, first.Ring, next.Rows[^1].Seq)).Rows);
    }

    [Fact]
    public void A_text_is_cut_to_one_line_of_100_characters()
    {
        Assert.Equal(100, DashboardsPluginInfo.MaxTableText);
        Assert.Equal(new string('x', 100), FieldValues.TableText(new string('x', 250)));
        Assert.Equal("two lines", FieldValues.TableText("two\nlines"));
        Assert.Equal(string.Empty, FieldValues.TableText(null));

        // Never half a character: a pair that does not fit goes as a whole.
        Assert.Equal(new string('x', 99), FieldValues.TableText(new string('x', 99) + "\U0001F600"));
    }

    [Fact]
    public async Task Rows_come_only_from_the_widgets_value_and_device()
    {
        using var host = new PluginHost();
        var plates = Table("plates");
        var gate = Table("gate", 4, Types.Gate, DashboardsPluginInfo.ActiveField);
        await SaveAsync(host, plates, gate);

        Plate(host, "other device", B);
        host.Feed.Publish(Types.Plate, A, PublishedEventKind.Fired, ("speed", "48")); // an event without the value
        host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", "20"));
        Plate(host, "W-123");
        host.Feed.Publish(Types.Gate, A, PublishedEventKind.On);
        host.Feed.Publish(Types.Gate, A, PublishedEventKind.Off);

        Assert.Equal(["W-123"], (await ReadAsync(host, plates)).Rows.Select(r => r.Text));
        Assert.Equal([DashboardTexts.On, DashboardTexts.Off], (await ReadAsync(host, gate)).Rows.Select(r => r.Text));
    }

    [Fact]
    public async Task Rows_live_in_memory_only_and_go_with_the_widget()
    {
        using var host = new PluginHost();
        var table = Table("t");
        var saved = await SaveAsync(host, table);
        Plate(host, "W-123");
        Assert.DoesNotContain("W-123", host.Settings.Values["dashboards"], StringComparison.Ordinal);
        var ring = (await ReadAsync(host, table)).Ring;

        // Another save of the same widget keeps the rows.
        saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(saved.Id, "Gate 2", saved.Version, [table]), CancellationToken.None);
        var kept = await ReadAsync(host, table);
        Assert.Equal((ring, 1), (kept.Ring, kept.Rows.Count));

        // Turned off and on (a restart): a new run, no rows; the page's old position is no use.
        await host.Plugin.StopAsync(CancellationToken.None);
        Assert.Equal(0, host.Plugin.Tables.Count);
        await host.Plugin.StartAsync(host.Context, CancellationToken.None);
        var fresh = await ReadAsync(host, table, ring, 1);
        Assert.NotEqual(ring, fresh.Ring);
        Assert.Empty(fresh.Rows);

        // A value no widget names is not recorded; a removed widget takes its rows along.
        await host.Plugin.SaveAsync(new SaveDashboardRequest(saved.Id, "Gate 2", saved.Version, []), CancellationToken.None);
        Plate(host, "W-456");
        Assert.Equal(0, host.Plugin.Tables.Count);
        var reply = await host.Plugin.TablesAsync(new TablesRequest([new TableRead("t", TableKey.Of(table)!, null, 0)]), CancellationToken.None);
        Assert.Empty(reply.Tables);
    }

    [Fact]
    public async Task A_dashboard_holds_at_most_10_tables_and_a_read_at_most_10()
    {
        using var host = new PluginHost();
        var tables = Enumerable.Range(0, DashboardsPluginInfo.MaxTables + 1).Select(i => Table("t" + i, i * 2) with { Width = 2, Height = 2 }).ToArray();
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(host, tables));
        Assert.Equal(DashboardRules.TooManyTables, refused.Message);
        await SaveAsync(host, tables[..DashboardsPluginInfo.MaxTables]);

        var reads = tables.Select(t => new TableRead(t.Id, TableKey.Of(t)!, null, 0)).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.TablesAsync(new TablesRequest(reads), CancellationToken.None));
    }

    [Fact]
    public void Reading_tables_needs_only_show_and_is_not_audited()
    {
        using var host = new PluginHost();
        Assert.Equal(PermissionKeys.Show, host.Plugin.RequiredPermission(DashboardsMethods.Tables));
        Assert.False(host.Plugin.IsAudited(DashboardsMethods.Tables));
    }

    [Fact]
    public async Task A_group_user_reads_the_rows_of_the_groups_devices_only_and_they_stop_when_a_device_leaves()
    {
        const string Trigger = "oadm.device-triggers.door";
        var fake = new Testing.FakeCoreContext();
        var inside = fake.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = fake.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = fake.FakeScopes.Add("Building A", [inside.Id]);
        var feed = new FakeEventSubscriptions();
        feed.TypeList.Add(new(new EventTypeInfo(Trigger, "Door", "Device triggers", EventKind.Stateless) { DeviceIds = [inside.Id, outside.Id] }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true));
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(feed);
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        using var plugin = new DashboardsPlugin(new FakeTimeProvider());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(fake, [inside.Id], "anna", key);

        await plugin.SaveAsync(new SaveDashboardRequest(null, "Doors", 0,
        [
            Table("in", 0, Trigger, "text", inside.Id),
            Table("out", 4, Trigger, "text", outside.Id),
        ]), CancellationToken.None);
        feed.Publish(Trigger, inside.Id, PublishedEventKind.Fired, ("text", "inside"));
        feed.Publish(Trigger, outside.Id, PublishedEventKind.Fired, ("text", "outside"));

        var mine = new TablesRequest([new TableRead("in", new TableKey(Trigger, inside.Id, "text"), null, 0)]);
        var reply = await plugin.TablesAsync(anna, mine, CancellationToken.None);
        Assert.Equal(["inside"], reply.Tables.SelectMany(t => t.Rows).Select(r => r.Text));

        // Another device answers like an unknown one.
        var other = new TablesRequest([new TableRead("out", new TableKey(Trigger, outside.Id, "text"), null, 0)]);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.TablesAsync(anna, other, CancellationToken.None));

        // A device that left the group: refused at once.
        fake.FakeScopes.Add("Building A", [], key);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.TablesAsync(anna, mine, CancellationToken.None));

        // The same JSON the page sends.
        var json = await plugin.InvokeAsync(fake.CallAsAllDevices(), DashboardsMethods.Tables, JsonSerializer.Serialize(other, DashboardsPluginInfo.Json), CancellationToken.None);
        Assert.Contains("outside", json, StringComparison.Ordinal);
    }
}
