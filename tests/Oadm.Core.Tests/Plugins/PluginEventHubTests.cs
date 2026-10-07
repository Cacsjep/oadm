using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

public sealed class PluginEventHubTests
{
    [Fact]
    public void PublishingWithoutWatchersDoesNothing()
    {
        var hub = new PluginEventHub();
        hub.For("p").Publish("state", "{}");
        Assert.Equal(0, hub.WatcherCount("p"));
    }

    [Fact]
    public async Task WatchersReceiveOnlyTheirPluginsEventsAndUnsubscribeOnCancel()
    {
        var hub = new PluginEventHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<PluginEvent>();
        var watch = Task.Run(async () =>
        {
            await foreach (var item in hub.WatchAsync("p", cts.Token))
            {
                received.Add(item);
                if (received.Count == 2)
                {
                    return;
                }
            }
        });
        await WaitAsync(() => hub.WatcherCount("p") == 1);

        hub.For("other").Publish("state", "{\"x\":0}");
        hub.For("p").Publish("state", "{\"x\":1}");
        hub.For("P").Publish("requests", null); // plugin ids are case-insensitive
        await watch;

        Assert.Equal([new PluginEvent("state", "{\"x\":1}"), new PluginEvent("requests", null)], received);
        await WaitAsync(() => hub.WatcherCount("p") == 0);
    }

    [Fact]
    public async Task SlowWatcherLosesTheOldestEventsAndNeverBlocksThePlugin()
    {
        var hub = new PluginEventHub();
        using var cts = new CancellationTokenSource();
        await using var enumerator = hub.WatchAsync("p", cts.Token).GetAsyncEnumerator(cts.Token);
        var first = enumerator.MoveNextAsync().AsTask(); // subscribes, then waits
        await WaitAsync(() => hub.WatcherCount("p") == 1);

        var publisher = hub.For("p");
        var total = PluginEventHub.QueueCapacity + 100;
        for (var i = 0; i < total; i++)
        {
            publisher.Publish("n", i.ToString(System.Globalization.CultureInfo.InvariantCulture)); // never blocks
        }

        Assert.True(await first);
        var received = new List<string?> { enumerator.Current.PayloadJson };
        while (received[^1] != (total - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            Assert.True(await enumerator.MoveNextAsync());
            received.Add(enumerator.Current.PayloadJson);
        }

        Assert.InRange(received.Count, 2, PluginEventHub.QueueCapacity + 1); // the oldest were dropped
        await cts.CancelAsync();
    }

    [Fact]
    public void OversizedEventsAreDropped()
    {
        var hub = new PluginEventHub();
        hub.Publish("p", "big", new string('x', IPluginEvents.MaxPayloadLength + 1));
        Assert.Throws<ArgumentException>(() => hub.Publish("p", " ", null));
    }

    [Fact]
    public async Task CorePluginHostHandsEachPluginItsPublisher()
    {
        var registry = new PluginRegistry();
        var plugin = new CapturingPlugin();
        registry.RegisterCorePlugin(plugin, new PluginOrigin("tests", "1.0.0", null));
        var hub = new PluginEventHub();
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(), events: hub);
        await host.StartAllAsync(CancellationToken.None);

        Assert.Same(hub, host.Events);
        Assert.NotNull(plugin.Events);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class CapturingPlugin : ICorePlugin
    {
        public IPluginEvents? Events { get; private set; }

        public string Id => "test.events";

        public string DisplayName => "Events";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Events = ctx.Events;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
