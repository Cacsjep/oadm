using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

/// <summary>
/// Subscribing to published events (the Dashboards plugin): every event the rule engine handles reaches subscribers, a
/// subscription starts with the events that are on, a slow or failing subscriber never holds up the engine, and a
/// plugin's subscriptions end with the plugin.
/// </summary>
public sealed class EventSubscriptionTests
{
    [Fact]
    public async Task EveryPublishedEventReachesASubscriberInOrder()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.48");
        var received = new Collector();
        using var subscription = rig.Engine.SubscriptionsFor("test.dashboards").Subscribe(received.Add);
        await rig.Engine.DrainAsync(CancellationToken.None);

        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true, new Dictionary<string, string> { ["port"] = "1" });
        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true, new Dictionary<string, string> { ["port"] = "2" }); // data changed
        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true, new Dictionary<string, string> { ["port"] = "2" }); // nothing new
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id, new Dictionary<string, string> { ["count"] = "7" });
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, false);
        rig.Publisher.SetState(EventRulesRig.ServerFlag, null, true);
        await rig.Engine.DrainAsync(CancellationToken.None);

        var events = await received.WaitForAsync(6);
        Assert.Equal(
            [
                (EventRulesRig.Blur, PublishedEventKind.On, ""),
                (EventRulesRig.Input, PublishedEventKind.On, "1"),
                (EventRulesRig.Input, PublishedEventKind.On, "2"),
                (EventRulesRig.Pulse, PublishedEventKind.Fired, "7"),
                (EventRulesRig.Blur, PublishedEventKind.Off, ""),
                (EventRulesRig.ServerFlag, PublishedEventKind.On, ""),
            ],
            events.Select(e => (e.TypeId, e.Kind, e.Data.Values.FirstOrDefault() ?? "")));
        Assert.All(events.Take(5), e => Assert.Equal(camera.Id, e.DeviceId));
        Assert.Null(events[5].DeviceId); // a server event
        Assert.All(events, e => Assert.False(e.FromSnapshot));
        Assert.Equal(rig.Time.GetUtcNow(), events[0].Time);
    }

    [Fact]
    public async Task ASubscriptionStartsWithTheEventsThatAreOnNow()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var a = await rig.AddDeviceAsync("10.0.0.48");
        var b = await rig.AddDeviceAsync("10.0.0.49");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [a.Id, b.Id]);
        rig.Publisher.SetState(EventRulesRig.Input, a.Id, true, new Dictionary<string, string> { ["port"] = "3" });
        rig.Publisher.SetState(EventRulesRig.ServerFlag, null, true);
        await rig.Engine.DrainAsync(CancellationToken.None);

        var received = new Collector();
        using var subscription = rig.Engine.SubscriptionsFor("test.dashboards").Subscribe(received.Add);
        await rig.Engine.DrainAsync(CancellationToken.None);
        rig.Publisher.SetState(EventRulesRig.Blur, a.Id, false);
        await rig.Engine.DrainAsync(CancellationToken.None);

        var events = await received.WaitForAsync(5);
        var snapshot = events.Take(4).ToList();
        Assert.All(snapshot, e => Assert.True(e.FromSnapshot));
        Assert.All(snapshot, e => Assert.Equal(PublishedEventKind.On, e.Kind));
        Assert.Equal(2, snapshot.Count(e => e.TypeId == EventRulesRig.Blur));
        Assert.Equal("3", Assert.Single(snapshot, e => e.TypeId == EventRulesRig.Input).Data["port"]);
        Assert.Null(Assert.Single(snapshot, e => e.TypeId == EventRulesRig.ServerFlag).DeviceId);
        Assert.Equal((EventRulesRig.Blur, PublishedEventKind.Off, false), (events[4].TypeId, events[4].Kind, events[4].FromSnapshot));
    }

    [Fact]
    public async Task ASubscriptionCountsAsWatchedUntilItEnds()
    {
        await using var rig = await EventRulesRig.StartAsync();
        Assert.False(rig.Publisher.IsWatched(EventRulesRig.Pulse));

        var subscription = rig.Engine.SubscriptionsFor("test.dashboards").Subscribe(_ => { });
        Assert.True(rig.Publisher.IsWatched(EventRulesRig.Pulse)); // sources must not skip events a plugin listens to

        subscription.Dispose();
        await rig.Engine.DrainAsync(CancellationToken.None);
        Assert.False(rig.Publisher.IsWatched(EventRulesRig.Pulse));
    }

    [Fact]
    public async Task AnEndedSubscriptionReceivesNothingMore()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.48");
        var received = new Collector();
        var subscription = rig.Engine.SubscriptionsFor("test.dashboards").Subscribe(received.Add);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await received.WaitForAsync(1);

        subscription.Dispose();
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await ((EventSubscription)subscription).Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(received.Snapshot());
    }

    [Fact]
    public async Task ASlowSubscriberDropsItsOldestEventsAndNeverWaitsTheEngine()
    {
        using var gate = new ManualResetEventSlim(false);
        var handled = new Collector();
        using var subscription = new EventSubscription("test.slow", e =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            handled.Add(e);
        }, _ => { }, capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            subscription.Post(Pulse(i)); // returns at once although the handler blocks
        }

        gate.Set();
        var events = await handled.WaitForAsync(3);
        // The newest event is handled last: once it is there, the queue is empty and nothing more can come.
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => handled.Snapshot() is { Count: > 0 } done && done[^1].Data["n"] == "9", "the newest event to be handled");
        var all = handled.Snapshot();

        Assert.True(subscription.Dropped >= 6, $"dropped {subscription.Dropped}");
        Assert.Equal("9", all[^1].Data["n"]); // the newest event is kept
        Assert.True(all.Count <= 4, $"handled {all.Count}");
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task AFailingHandlerKeepsItsSubscription()
    {
        var handled = new Collector();
        using var subscription = new EventSubscription("test.failing", e =>
        {
            if (e.Data["n"] == "0")
            {
                throw new InvalidOperationException("broken plugin");
            }

            handled.Add(e);
        }, _ => { });

        subscription.Post(Pulse(0));
        subscription.Post(Pulse(1));

        Assert.Equal("1", Assert.Single(await handled.WaitForAsync(1)).Data["n"]);
    }

    [Fact]
    public async Task APluginsSubscriptionsEndWithThePlugin()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var pluginSubscriptions = new PluginEventSubscriptions(rig.Engine.SubscriptionsFor("test.dashboards"));
        var first = (EventSubscription)pluginSubscriptions.Subscribe(_ => { });
        var second = (EventSubscription)pluginSubscriptions.Subscribe(_ => { });

        pluginSubscriptions.Dispose(); // the host does this when the plugin stops, fails or is turned off

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => pluginSubscriptions.Subscribe(_ => { }));
        await rig.Engine.DrainAsync(CancellationToken.None);
        Assert.False(rig.Publisher.IsWatched(EventRulesRig.Pulse));
    }

    [Fact]
    public async Task SubscribersSeeTheEventTypesAndWhenTheyChange()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var subscriptions = rig.Engine.SubscriptionsFor("test.dashboards");
        var input = Assert.Single(subscriptions.Types, t => t.Info.Id == EventRulesRig.Input);
        Assert.Equal((EventRulesRig.SourceId, "Test source", true), (input.SourceId, input.SourceName, input.Available));
        Assert.Equal("port", Assert.Single(input.Info.DataFields).Name);
        Assert.Contains(subscriptions.Types, t => t.SourceId == "oadm.device"); // OADM's own events too

        var changes = 0;
        EventHandler counter = (_, _) => changes++;
        subscriptions.TypesChanged += counter;
        rig.Registry.SetEnabled("test.source-package", false);
        Assert.Equal(1, changes);
        Assert.False(Assert.Single(subscriptions.Types, t => t.Info.Id == EventRulesRig.Input).Available);
        subscriptions.TypesChanged -= counter;
        rig.Registry.SetEnabled("test.source-package", true);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AStoppedPluginHearsOfNoMoreTypeChanges()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var pluginSubscriptions = new PluginEventSubscriptions(rig.Engine.SubscriptionsFor("test.dashboards"));
        var changes = 0;
        pluginSubscriptions.TypesChanged += (_, _) => changes++;
        Assert.NotEmpty(pluginSubscriptions.Types);
        rig.Registry.SetEnabled("test.source-package", false);
        Assert.Equal(1, changes);

        pluginSubscriptions.Dispose();
        rig.Registry.SetEnabled("test.source-package", true);
        Assert.Equal(1, changes);
    }

    private static PublishedEvent Pulse(int n) =>
        new(EventRulesRig.Pulse, Guid.NewGuid(), PublishedEventKind.Fired, new Dictionary<string, string> { ["n"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) }, DateTimeOffset.UtcNow);

    /// <summary>Collects events from the subscription's own loop and lets the test wait for a count.</summary>
    private sealed class Collector
    {
        private readonly List<PublishedEvent> _events = [];

        public void Add(PublishedEvent published)
        {
            lock (_events)
            {
                _events.Add(published);
            }
        }

        public List<PublishedEvent> Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public async Task<List<PublishedEvent>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Snapshot().Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            return Snapshot();
        }
    }
}
