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
        ctx.Jobs.Returns(fake.FakeJobs);
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

    [Fact]
    public async Task Group_users_never_see_events_of_other_owners_without_a_device_list()
    {
        var (fake, feed, plugin, a, _, keyA, _) = await StartAsync();
        using var _ = plugin;
        feed.TypeList.AddRange(
        [
            new(new EventTypeInfo("oadm.device.unreachable", "Device unreachable", "Device status", EventKind.Stateful), "oadm.device", "Built in", true),
            new(new EventTypeInfo("oadm.compliance.check.1", "Compliance: Vault cameras of Building B", "Compliance", EventKind.Stateful), "oadm.compliance", "Compliance", true),
            new(new EventTypeInfo("acme.any", "Anything", "Acme", EventKind.Stateful), "acme", "Acme", true),
        ]);
        feed.ChangeTypes(_ => { });
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);

        var catalog = Read<CatalogReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Catalog, null, CancellationToken.None));
        Assert.Contains(catalog.Events, e => e.TypeId == "oadm.device.unreachable");
        Assert.DoesNotContain(catalog.Events, e => e.TypeId is "oadm.compliance.check.1" or "acme.any");
        Assert.Equal([], Read<DevicesReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Devices, Json(new DevicesRequest("oadm.compliance.check.1")), CancellationToken.None)).DeviceIds!);

        // A watch of another owner's check on the group's own device is left out.
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey("oadm.compliance.check.1", a.Id), new ValueKey("oadm.device.unreachable", a.Id)])), CancellationToken.None);
        Assert.Equal(1, plugin.WatchedCount);
        Assert.Equal(5, Read<CatalogReply>(await plugin.InvokeAsync(fake.CallAsAllDevices(), DashboardsMethods.Catalog, null, CancellationToken.None)).Events.Count(e => e.TypeId != ServerFlag));
    }

    [Fact]
    public async Task Values_stop_at_once_when_a_device_leaves_the_group()
    {
        var (fake, feed, plugin, a, b, keyA, time) = await StartAsync();
        using var _ = plugin;
        fake.FakeScopes.Add("Building A", [a.Id, b.Id], keyA);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id, b.Id], "anna", keyA);
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id), new ValueKey(OtherTrigger, b.Id)])), CancellationToken.None);

        // A tag change moves b out of the group; the page has not renewed its watch yet.
        fake.FakeScopes.Add("Building A", [a.Id], keyA);
        feed.Publish(OtherTrigger, b.Id, PublishedEventKind.On);
        feed.Publish(Trigger, a.Id, PublishedEventKind.On);
        time.Advance(DashboardsPluginInfo.PushInterval);
        await Testing.Eventually.TrueAsync(() => fake.EventsSeenBy(anna).Any(e => e.Topic == DashboardsMethods.ValuesTopic), "the push after the change");
        var seen = fake.EventsSeenBy(anna).Where(e => e.Topic == DashboardsMethods.ValuesTopic).SelectMany(e => Read<ValuesReply>(e.PayloadJson).Values).ToList();
        Assert.All(seen, v => Assert.Equal(a.Id, v.DeviceId));

        // The renewal names the device that left: refused, and the old watch is gone.
        var now = new SameSession(Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA), anna.SessionId);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.InvokeAsync(now, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id), new ValueKey(OtherTrigger, b.Id)])), CancellationToken.None));
        Assert.Equal(0, plugin.WatchedCount);
    }

    [Fact]
    public async Task Group_users_never_see_the_device_triggers_of_other_owners_on_their_devices()
    {
        var (fake, feed, plugin, a, _, keyA, _) = await StartAsync();
        using var _ = plugin;
        var keyB = fake.FakeScopes.Add("Building B", [a.Id]); // a is in both groups
        feed.TypeList.AddRange(
        [
            new(new EventTypeInfo("oadm.device-triggers.mine", "Mine", "Device triggers", EventKind.Stateful) { DeviceIds = [a.Id], OwnerScopeKey = keyA }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true),
            new(new EventTypeInfo("oadm.device-triggers.admins", "Admins", "Device triggers", EventKind.Stateful) { DeviceIds = [a.Id], OwnerScopeKey = ICallContext.AllDevicesScopeKey }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true),
            new(new EventTypeInfo("oadm.device-triggers.theirs", "Theirs", "Device triggers", EventKind.Stateful) { DeviceIds = [a.Id], OwnerScopeKey = keyB }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true),
        ]);
        feed.ChangeTypes(_ => { });
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);

        var catalog = Read<CatalogReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Catalog, null, CancellationToken.None));
        Assert.Contains(catalog.Events, e => e.TypeId == "oadm.device-triggers.mine");
        Assert.DoesNotContain(catalog.Events, e => e.TypeId is "oadm.device-triggers.admins" or "oadm.device-triggers.theirs");
        Assert.Equal([], Read<DevicesReply>(await plugin.InvokeAsync(anna, DashboardsMethods.Devices, Json(new DevicesRequest("oadm.device-triggers.theirs")), CancellationToken.None)).DeviceIds!);

        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey("oadm.device-triggers.mine", a.Id), new ValueKey("oadm.device-triggers.admins", a.Id), new ValueKey("oadm.device-triggers.theirs", a.Id)])), CancellationToken.None);
        Assert.Equal(1, plugin.WatchedCount);

        // A user with all devices sees every trigger.
        var all = Read<CatalogReply>(await plugin.InvokeAsync(fake.CallAsAllDevices(), DashboardsMethods.Catalog, null, CancellationToken.None));
        Assert.Contains(all.Events, e => e.TypeId == "oadm.device-triggers.theirs");
    }

    [Fact]
    public async Task A_group_users_watches_end_when_the_users_devices_change()
    {
        var (fake, feed, plugin, a, _, keyA, time) = await StartAsync();
        using var _ = plugin;
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id)])), CancellationToken.None);
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("tab 2", [new ValueKey(Trigger, a.Id)])), CancellationToken.None);
        Assert.Equal(1, plugin.SessionJobCount); // one job per session
        var job = Assert.Single(fake.FakeJobs.All);
        Assert.Equal(anna.SessionId, job.Owner.SessionId);

        // The host ends the session's jobs: anna was moved to another group. Her watches go at once.
        job.Cancel();
        await Testing.Eventually.TrueAsync(() => plugin.WatchedCount == 0 && plugin.SessionJobCount == 0, "the watches of the old devices ended");
        var admin = fake.CallAsAllDevices();
        await plugin.InvokeAsync(admin, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id)])), CancellationToken.None);
        feed.Publish(Trigger, a.Id, PublishedEventKind.On);
        time.Advance(DashboardsPluginInfo.PushInterval);
        await Testing.Eventually.TrueAsync(() => fake.EventsSeenBy(admin).Any(e => e.Topic == DashboardsMethods.ValuesTopic), "the push went out");
        Assert.DoesNotContain(fake.EventsSeenBy(anna), e => e.Topic == DashboardsMethods.ValuesTopic);

        // A new watch gets a new job; the last unwatch ends it.
        await plugin.InvokeAsync(anna, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, a.Id)])), CancellationToken.None);
        var second = Assert.Single(fake.FakeJobs.All, j => j != job);
        await plugin.InvokeAsync(anna, DashboardsMethods.Unwatch, Json(new UnwatchRequest("page")), CancellationToken.None);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, plugin.SessionJobCount);
    }

    [Fact]
    public async Task An_administrator_saves_a_groups_dashboard_whose_device_left_the_group()
    {
        var (fake, _, plugin, a, b, keyA, _) = await StartAsync();
        using var _ = plugin;
        var c = fake.Add(new Testing.FakeDevice { Address = "10.0.0.3" });
        fake.FakeScopes.Add("Building A", [a.Id, b.Id], keyA);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id, b.Id], "anna", keyA);
        var admin = fake.CallAsAllDevices();
        var doors = Read<Dashboard>(await plugin.InvokeAsync(anna, DashboardsMethods.Save, Json(new SaveDashboardRequest(null, "Doors", 0, [Widget(a.Id), Widget(b.Id) with { Id = "2", X = 4 }])), CancellationToken.None));

        fake.FakeScopes.Add("Building A", [a.Id], keyA); // b left the group
        var saved = Read<Dashboard>(await plugin.InvokeAsync(admin, DashboardsMethods.Save, Json(new SaveDashboardRequest(doors.Id, "Doors 2", doors.Version, doors.Widgets)), CancellationToken.None));
        Assert.Equal(1, saved.Dropped);
        Assert.Equal([a.Id], saved.Widgets.Select(w => w.DeviceId));
        Assert.Equal(keyA, saved.Owner);
        Assert.DoesNotContain("dropped", fake.PluginSettings.Values.Values.First(), StringComparison.OrdinalIgnoreCase); // never stored

        // A device the administrator adds that is not in the group, and the group's own users, are still refused.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.InvokeAsync(admin, DashboardsMethods.Save, Json(new SaveDashboardRequest(doors.Id, "Doors 3", saved.Version, [.. saved.Widgets, Widget(c.Id) with { Id = "3", X = 8 }])), CancellationToken.None));
        var now = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => plugin.InvokeAsync(now, DashboardsMethods.Save, Json(new SaveDashboardRequest(doors.Id, "Doors 3", saved.Version, [.. saved.Widgets, Widget(b.Id) with { Id = "2", X = 4 }])), CancellationToken.None));
    }

    /// <summary>The plugin over a fake host with two devices (a in Building A at a site, b outside) and their triggers.</summary>
    private static async Task<(Testing.FakeCoreContext Fake, FakeEventSubscriptions Feed, DashboardsPlugin Plugin, Testing.FakeDevice A, Testing.FakeDevice B, string KeyA, FakeTimeProvider Time)> StartAsync()
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
        ]);
        var time = new FakeTimeProvider();
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(feed);
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        var plugin = new DashboardsPlugin(time);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (fake, feed, plugin, a, b, keyA, time);
    }

    /// <summary>A call of the same login session with the user's devices as they are now.</summary>
    private sealed class SameSession(ICallContext inner, string sessionId) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => sessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public IDeviceRepository Devices => inner.Devices;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

        public IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => inner.Has(permission);

        public bool Contains(IDeviceInfo device) => inner.Contains(device);
    }

    private static Widget Widget(Guid device) => new() { Id = "1", Kind = WidgetKind.OnOff, TypeId = Trigger, Field = DashboardsPluginInfo.ActiveField, DeviceId = device };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, DashboardsPluginInfo.Json);

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json!, DashboardsPluginInfo.Json)!;
}
