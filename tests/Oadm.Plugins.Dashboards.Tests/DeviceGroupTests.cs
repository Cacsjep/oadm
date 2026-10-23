using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Shared objects): every dashboard belongs to the device scope of the user who
/// made it; group users see and change only their group's dashboards with the group's devices, get only the events of their
/// devices and sites, and only the values their own pages watch.
/// </summary>
public sealed class DeviceGroupTests
{
    private const string Trigger = "oadm.device-triggers.door";
    private const string OtherTrigger = "oadm.device-triggers.vault";
    private const string ServerFlag = "acme.flag";

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        using var plugin = new DashboardsPlugin(new FakeTimeProvider());
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(DashboardsMethods.Save, """{"name":"Doors","version":0,"widgets":[{"id":"1","kind":"number","x":0,"y":0,"width":4,"height":4,"typeId":"t","field":"f","deviceId":"{inside}"},{"id":"2","kind":"video","x":4,"y":0,"width":4,"height":4,"deviceId":"{device}"}]}""")
            .Method(DashboardsMethods.Watch, """{"watchId":"page","keys":[{"typeId":"t","deviceId":"{inside}"},{"typeId":"t","deviceId":"{device}"}]}""")
            .WithoutDevices(DashboardsMethods.List, "the dashboards of the caller's own group, widgets filtered to the group")
            .WithoutDevices(DashboardsMethods.Delete, "only a dashboard of the caller's own group", """{"id":"none"}""")
            .WithoutDevices(DashboardsMethods.Catalog, "the events of the caller's devices and sites")
            .WithoutDevices(DashboardsMethods.Devices, "the devices of an event, filtered to the caller's devices", """{"typeId":"t"}""")
            .WithoutDevices(DashboardsMethods.Unwatch, "only a watch of the caller's own session", """{"watchId":"page"}""")
            .RunAsync();
    }

    [Fact]
    public async Task GroupsSeeOnlyTheirDashboardsEventsAndValues()
    {
        var fake = new Testing.FakeCoreContext();
        var site = Guid.NewGuid();
        var a = fake.Add(new Testing.FakeDevice { Address = "10.0.0.1", SiteId = site });
        var b = fake.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = fake.FakeScopes.Add("Building A", [a.Id]);
        var feed = new FakeEventSubscriptions();
        feed.TypeList.AddRange(
        [
            new(new EventTypeInfo(Trigger, "Door", "Device triggers", EventKind.Stateful) { DeviceIds = [a.Id] }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true),
            new(new EventTypeInfo(OtherTrigger, "Vault", "Device triggers", EventKind.Stateful) { DeviceIds = [b.Id] }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true),
            new(new EventTypeInfo("oadm.site.offline." + site.ToString("N"), "Site offline: Building A", "Sites", EventKind.Stateful, EventScope.Server), "oadm.site", "Sites", true),
            new(new EventTypeInfo(ServerFlag, "Flag", "Acme", EventKind.Stateful, EventScope.Server), "acme", "Acme", true),
        ]);
        var time = new FakeTimeProvider();
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(feed);
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        using var plugin = new DashboardsPlugin(time);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        var admin = fake.CallAsAllDevices();

        // The catalog: the group's trigger and site, nothing else.
        var catalog = Read<CatalogReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Catalog, null, CancellationToken.None));
        Assert.Equal(new[] { Trigger, "oadm.site.offline." + site.ToString("N") }.Order(), catalog.Events.Select(e => e.TypeId).Order());
        Assert.Equal(4, Read<CatalogReply>(await plugin.InvokeAsync(admin, DashboardsMethods.Catalog, null, CancellationToken.None)).Events.Count);
        Assert.Equal([], Read<DevicesReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Devices, Json(new DevicesRequest(OtherTrigger)), CancellationToken.None)).DeviceIds!);

        // Dashboards per group; the administrator keeps the owner.
        var doors = Read<Dashboard>(await plugin.InvokeAsync(anna, DashboardsMethods.Save, Json(new SaveDashboardRequest(null, "Doors", 0, [Widget(a.Id)])), CancellationToken.None));
        Assert.Equal(keyA, doors.Owner);
        await plugin.InvokeAsync(admin, DashboardsMethods.Save, Json(new SaveDashboardRequest(null, "Doors", 0, [Widget(b.Id)])), CancellationToken.None);
        Assert.Equal([doors.Id], Read<DashboardList>(await plugin.InvokeAsync(anna, DashboardsMethods.List, null, CancellationToken.None)).Dashboards.Select(d => d.Id));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.InvokeAsync(admin, DashboardsMethods.Save, Json(new SaveDashboardRequest(doors.Id, "Doors", doors.Version, [Widget(b.Id)])), CancellationToken.None));
        Assert.Equal(keyA, Read<Dashboard>(await plugin.InvokeAsync(admin, DashboardsMethods.Save, Json(new SaveDashboardRequest(doors.Id, "Doors 2", doors.Version, [Widget(a.Id)])), CancellationToken.None)).Owner);

        // Values: the group's session gets only what it watches; a server event it does not see is not watched.
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id), new ValueKey(ServerFlag, null)])), CancellationToken.None);
        await plugin.InvokeAsync(admin, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(OtherTrigger, b.Id)])), CancellationToken.None);
        Assert.Equal(2, plugin.WatchedCount); // the same watch id in two sessions: two watches, the flag left out
        feed.Publish(Trigger, a.Id, PublishedEventKind.On);
        feed.Publish(OtherTrigger, b.Id, PublishedEventKind.On);
        time.Advance(DashboardsPluginInfo.PushInterval);
        await Testing.Eventually.TrueAsync(() => fake.EventsSeenBy(anna).Any(e => e.Topic == DashboardsMethods.ValuesTopic), "the group's values were pushed");
        var seen = fake.EventsSeenBy(anna).Where(e => e.Topic == DashboardsMethods.ValuesTopic).SelectMany(e => Read<ValuesReply>(e.PayloadJson).Values).ToList();
        Assert.All(seen, v => Assert.Equal(a.Id, v.DeviceId));
        Assert.Contains(fake.EventsSeenBy(admin), e => e.Topic == DashboardsMethods.ValuesTopic && e.PayloadJson!.Contains(b.Id.ToString(), StringComparison.Ordinal));
    }

    private static Widget Widget(Guid device) => new() { Id = "1", Kind = WidgetKind.OnOff, TypeId = Trigger, Field = DashboardsPluginInfo.ActiveField, DeviceId = device };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, DashboardsPluginInfo.Json);

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json!, DashboardsPluginInfo.Json)!;
}
