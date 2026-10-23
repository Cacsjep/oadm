using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

using Eventually = Oadm.Sdk.Testing.Eventually;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The audiences of plugin events (docs/spec/device-scope.md, Plugins): each event reaches only the watchers it covers,
/// rows of a row event are filtered per watcher, and the old publish reaches only watchers with all devices.
/// </summary>
public sealed class PluginEventAudienceTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public async Task EachEventReachesOnlyTheWatchersItsAudienceCovers()
    {
        var hub = new PluginEventHub();
        var admin = new Watching(hub, Watcher("admin", "s-admin", ICallContext.AllDevicesScopeKey, all: true));
        var anna = new Watching(hub, Watcher("anna", "s-anna", "group-a", all: false, A));
        var anna2 = new Watching(hub, Watcher("anna", "s-anna-2", "group-a", all: false, A));
        var ben = new Watching(hub, Watcher("ben", "s-ben", "group-b", all: false, B));
        await Eventually.TrueAsync(() => hub.WatcherCount("p") == 4, "four watchers");

        var publisher = hub.For("p");
        publisher.Publish("old", "1"); // the old publish: all devices only
        publisher.Publish("all", "2", PluginEventAudience.AllDevices);
        publisher.Publish("device", "3", PluginEventAudience.Device(A));
        publisher.Publish("rows", "[\"a1\",\"b1\",\"a2\"]", PluginEventAudience.DeviceRows([A, B, A]));
        publisher.Publish("user", "4", PluginEventAudience.User("ANNA"));
        publisher.Publish("session", "5", PluginEventAudience.Session("s-anna-2"));
        publisher.Publish("scope", "6", PluginEventAudience.DeviceScope("group-b"));
        publisher.Publish("rows-none", "[\"x\"]", PluginEventAudience.DeviceRows([Guid.NewGuid()]));
        publisher.Publish("rows-bad", "{\"not\":\"an array\"}", PluginEventAudience.DeviceRows([A]));
        publisher.Publish("end", null, PluginEventAudience.User("admin"));
        publisher.Publish("end", null, PluginEventAudience.User("anna"));
        publisher.Publish("end", null, PluginEventAudience.User("ben"));

        Assert.Equal(["old:1", "all:2", "device:3", "rows:[\"a1\",\"b1\",\"a2\"]", "rows-none:[\"x\"]", "rows-bad:{\"not\":\"an array\"}", "end:"], await admin.UntilEndAsync());
        Assert.Equal(["device:3", "rows:[\"a1\",\"a2\"]", "user:4", "end:"], await anna.UntilEndAsync());
        Assert.Equal(["device:3", "rows:[\"a1\",\"a2\"]", "user:4", "session:5", "end:"], await anna2.UntilEndAsync());
        Assert.Equal(["rows:[\"b1\"]", "scope:6", "end:"], await ben.UntilEndAsync());
    }

    [Fact]
    public async Task DeviceEventsOfOtherDevicesNeverPushAGroupWatchersOwnEventsOutOfItsQueue()
    {
        var hub = new PluginEventHub();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = new HashSet<Guid> { A };
        var watcher = new PluginWatcher("anna", "s-anna", "group-a", false, async _ =>
        {
            await gate.Task; // the reader stays on its first event while the others are published
            return visible;
        })
        {
            CachedIds = () => visible,
        };
        var anna = new Watching(hub, watcher);
        await Eventually.TrueAsync(() => hub.WatcherCount("p") == 1, "anna watches");

        var publisher = hub.For("p");
        publisher.Publish("first", "1", PluginEventAudience.Device(A));
        publisher.Publish("mine", "2", PluginEventAudience.Device(A));
        for (var i = 0; i < PluginEventHub.QueueCapacity * 2; i++)
        {
            publisher.Publish("other", "x", PluginEventAudience.Device(B));
            publisher.Publish("other-rows", "[\"x\"]", PluginEventAudience.DeviceRows([B]));
        }

        publisher.Publish("end", null, PluginEventAudience.User("anna"));
        gate.SetResult();

        Assert.Equal(["first:1", "mine:2", "end:"], await anna.UntilEndAsync());
    }

    private static PluginWatcher Watcher(string user, string session, string scope, bool all, params Guid[] visible) =>
        new(user, session, scope, all, _ => Task.FromResult<IReadOnlySet<Guid>>(visible.ToHashSet()));

    /// <summary>A watcher reading in the background until it got the "end" event.</summary>
    private sealed class Watching
    {
        private readonly Task<List<string>> _reading;

        public Watching(PluginEventHub hub, PluginWatcher watcher)
        {
            _reading = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var seen = new List<string>();
                await foreach (var item in hub.WatchAsync("p", watcher, cts.Token))
                {
                    seen.Add(item.Topic + ":" + item.PayloadJson);
                    if (item.Topic == "end")
                    {
                        break;
                    }
                }

                return seen;
            });
        }

        public Task<List<string>> UntilEndAsync() => _reading;
    }
}
