using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>
/// Dashboards with device groups at the device limit (CLAUDE.md, scale): the pushes of the values and catalogs of the group
/// pages never read the device table; each group's devices are read once and again only when the group's devices change.
/// The watches of a session stay bound to its group, and one session never takes the slots of the others.
/// </summary>
public sealed class GroupScaleTests(ITestOutputHelper output)
{
    private const string Trigger = "oadm.device-triggers.door";
    private const int Devices = 30_000;
    private const int Groups = 5;
    private const int SessionsPerGroup = 10;

    [Fact]
    public async Task Pushes_to_group_pages_never_read_the_device_table_again() => await PushRoundsAsync(rounds: 20);

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Pushes_to_group_pages_at_30000_devices_are_fast()
    {
        var elapsed = await PushRoundsAsync(rounds: 100);
        output.WriteLine($"100 pushes to {Groups * SessionsPerGroup} group pages of {Groups} groups at 30,000 devices: {elapsed.TotalMilliseconds:0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"100 pushes took {elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task A_change_of_a_group_reads_its_devices_again()
    {
        using var setup = await SetUpAsync();
        var (plugin, scopes, sessions) = (setup.Plugin, setup.Scopes, setup.Sessions);
        await WatchAllAsync(setup);
        var reads = scopes.Reads;

        // A device leaves group 0: its values stop at once (the host's check), before the group's change is raised.
        var leaving = setup.Members[0][0];
        scopes.Move(setup.Keys[0], leaving, into: false);
        setup.Feed.Publish(Trigger, leaving, PublishedEventKind.On);
        await plugin.TickAsync();
        Assert.DoesNotContain(setup.Fake.EventsSeenBy(sessions[0]), e => e.Topic == DashboardsMethods.ValuesTopic && e.PayloadJson!.Contains(leaving.ToString(), StringComparison.Ordinal));

        // The group's change: its catalog and watches use the group's devices read once more.
        scopes.RaiseChanged(setup.Keys[0]);
        setup.Feed.ChangeTypes(_ => { });
        setup.Time.Advance(TimeSpan.FromSeconds(1));
        await plugin.TickAsync();
        Assert.Equal(reads + 1, scopes.Reads);
        await plugin.TickAsync();
        Assert.Equal(reads + 1, scopes.Reads);
    }

    [Fact]
    public async Task A_sessions_watches_of_its_old_group_end_when_it_watches_for_another_group()
    {
        using var setup = await SetUpAsync();
        var (plugin, keys, members) = (setup.Plugin, setup.Keys, setup.Members);
        var first = setup.Sessions[0];
        await plugin.InvokeAsync(first, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Trigger, members[0][0])])), CancellationToken.None);
        var oldJob = Assert.Single(setup.Fake.FakeJobs.All);

        // The user was moved to group 1; the host has not ended the old job yet when the next tab watches.
        var moved = new SameSession(Testing.FakeCallContext.WithGroup(setup.Fake, members[1], "user 0", keys[1]), first.SessionId);
        await plugin.InvokeAsync(moved, DashboardsMethods.Watch, Json(new WatchRequest("tab 2", [new ValueKey(Trigger, members[1][0])])), CancellationToken.None);
        await oldJob.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, plugin.WatchedCount);
        Assert.Equal(1, plugin.SessionJobCount);

        // A value of the old group's device never reaches the session.
        setup.Feed.Publish(Trigger, members[0][0], PublishedEventKind.On);
        setup.Feed.Publish(Trigger, members[1][0], PublishedEventKind.On);
        await plugin.TickAsync();
        var seen = setup.Fake.EventsSeenBy(first).Where(e => e.Topic == DashboardsMethods.ValuesTopic).SelectMany(e => Read<ValuesReply>(e.PayloadJson).Values).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, v => Assert.Equal(members[1][0], v.DeviceId));
    }

    [Fact]
    public async Task One_session_keeps_at_most_eight_watches_and_never_takes_the_slots_of_others()
    {
        var host = new PluginHost();
        using var _ = host;
        var admin = Testing.FakeCallContext.Over(Substitute.For<IDeviceRepository>());
        for (var i = 0; i < DashboardsPluginInfo.MaxWatches + 5; i++)
        {
            await host.Plugin.InvokeAsync(admin, DashboardsMethods.Watch, Json(new WatchRequest("page " + i, [new ValueKey(Types.Temperature, Guid.NewGuid())])), CancellationToken.None);
            host.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(DashboardsPluginInfo.MaxWatchesPerSession, host.Plugin.WatchedCount); // the oldest made room

        var other = Testing.FakeCallContext.Over(Substitute.For<IDeviceRepository>(), userName: "other");
        await host.Plugin.InvokeAsync(other, DashboardsMethods.Watch, Json(new WatchRequest("page", [new ValueKey(Types.Gate, null)])), CancellationToken.None);
        Assert.Equal(DashboardsPluginInfo.MaxWatchesPerSession + 1, host.Plugin.WatchedCount);
    }

    /// <summary>Every group page watches; then rounds of changed values and catalogs. Returns the time of the rounds.</summary>
    private static async Task<TimeSpan> PushRoundsAsync(int rounds)
    {
        using var setup = await SetUpAsync();
        await WatchAllAsync(setup);
        var reads = setup.Scopes.Reads;
        Assert.True(reads <= Groups, $"{reads} reads of the groups' devices to start the watches");

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var group in setup.Members)
            {
                setup.Feed.Publish(Trigger, group[round % 100], PublishedEventKind.On, ("n", round.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            setup.Time.Advance(TimeSpan.FromSeconds(1)); // the catalog goes out too (a new field the first time)
            await setup.Plugin.TickAsync();
        }

        watch.Stop();
        Assert.Equal(reads, setup.Scopes.Reads); // no read of the device table per push
        var seen = setup.Fake.EventsSeenBy(setup.Sessions[0]).Where(e => e.Topic == DashboardsMethods.ValuesTopic).SelectMany(e => Read<ValuesReply>(e.PayloadJson).Values).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, v => Assert.Contains(v.DeviceId!.Value, setup.Members[0]));
        return watch.Elapsed;
    }

    private static async Task WatchAllAsync(Setup setup)
    {
        for (var i = 0; i < setup.Sessions.Count; i++)
        {
            var group = setup.Members[i % Groups];
            IReadOnlyList<ValueKey> keys = [.. group.Take(100).Select(id => new ValueKey(Trigger, id))];
            await setup.Plugin.InvokeAsync(setup.Sessions[i], DashboardsMethods.Watch, Json(new WatchRequest("page", keys)), CancellationToken.None);
        }
    }

    private static async Task<Setup> SetUpAsync()
    {
        var fake = new Testing.FakeCoreContext();
        var site = Guid.NewGuid();
        var devices = Enumerable.Range(0, Devices).Select(i => (IDeviceInfo)new TestDevice($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "Q1656") { SiteId = i % 2 == 0 ? site : null }).ToList();
        var scopes = new CountingDeviceScopes(devices);
        var members = new List<List<Guid>>();
        var keys = new List<string>();
        for (var g = 0; g < Groups; g++)
        {
            var ids = devices.Where((_, i) => i % Groups == g).Select(d => d.Id).ToList();
            members.Add(ids);
            keys.Add(scopes.Add(ids));
        }

        var feed = new FakeEventSubscriptions();
        feed.TypeList.Add(new(new EventTypeInfo(Trigger, "Door", "Device triggers", EventKind.Stateful) { DeviceIds = [.. devices.Select(d => d.Id)] }, DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true));
        var time = new FakeTimeProvider();
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(feed);
        ctx.DeviceScopes.Returns(scopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        var plugin = new DashboardsPlugin(time);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var sessions = Enumerable.Range(0, Groups * SessionsPerGroup)
            .Select(i => Testing.FakeCallContext.WithGroup(fake, members[i % Groups], "user " + i, keys[i % Groups])).ToList();
        return new Setup(fake, feed, plugin, scopes, time, members, keys, sessions);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, DashboardsPluginInfo.Json);

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json!, DashboardsPluginInfo.Json)!;

    private sealed record Setup(
        Testing.FakeCoreContext Fake,
        FakeEventSubscriptions Feed,
        DashboardsPlugin Plugin,
        CountingDeviceScopes Scopes,
        FakeTimeProvider Time,
        List<List<Guid>> Members,
        List<string> Keys,
        List<Testing.FakeCallContext> Sessions) : IDisposable
    {
        public void Dispose() => Plugin.Dispose();
    }

    /// <summary>A call of the same login session with other devices (the user was moved to another group).</summary>
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
}
