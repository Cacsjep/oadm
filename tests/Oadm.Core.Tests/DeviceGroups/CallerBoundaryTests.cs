using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The background boundary (docs/spec/device-scope.md, Enforcement): the caller of a call never flows into task runs, the
/// handlers of host events or work started detached, so nothing outlives the call with the user's rights.
/// </summary>
public sealed class CallerBoundaryTests
{
    private static AuthenticatedCaller Caller() =>
        new(Guid.NewGuid(), "anna", BuiltInRoles.OperatorId, PermissionSet.All, "local", "pc", "hash") { Devices = CallerDevices.All };

    [Fact]
    public async Task SuppressRunDetachedAndRaiseClearTheCaller()
    {
        using (CallerContext.Use(Caller()))
        {
            Assert.NotNull(CallerContext.Current);
            using (CallerContext.Suppress())
            {
                Assert.Null(CallerContext.Current);
            }

            Assert.NotNull(CallerContext.Current);
            AuthenticatedCaller? inside = Caller();
            await CallerContext.RunDetached(() =>
            {
                inside = CallerContext.Current;
                return Task.CompletedTask;
            });
            Assert.Null(inside);

            AuthenticatedCaller? handler = Caller();
            EventHandler raise = (_, _) => handler = CallerContext.Current;
            CallerContext.Raise(raise, null, EventArgs.Empty);
            Assert.Null(handler);
            Assert.NotNull(CallerContext.Current);
        }
    }

    [Fact]
    public async Task ATaskStartedInAUsersCallRunsWithoutTheCaller()
    {
        var devices = new FakeDeviceRepository();
        var registry = new PluginRegistry();
        AuthenticatedCaller? seen = Caller();
        Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.caller", (_, _, _) =>
        {
            seen = CallerContext.Current;
            return Task.CompletedTask;
        }), new PluginOrigin("test", "1.0.0", null)));
        var store = new InMemoryTaskStore();
        await using var engine = new TaskEngine(store, registry, devices, new FakeVapixClientFactory());
        var id = devices.Add();

        Guid taskId;
        using (CallerContext.Use(Caller()))
        {
            taskId = (await engine.RunAsync("t.caller", [id], null, "x", CancellationToken.None))[0];
        }

        await Eventually.TrueAsync(async () => (await store.GetAsync(taskId, CancellationToken.None))?.State == TaskState.Done, "the task to finish");
        Assert.Null(seen);
        Assert.StartsWith("anna@pc", (await store.GetAsync(taskId, CancellationToken.None))!.Owner, StringComparison.Ordinal); // the owner is still the user
    }

    [Fact]
    public void TheHandlersOfTheDeviceFeedNeverSeeTheCaller()
    {
        var feed = new DeviceChangeFeed();
        AuthenticatedCaller? seen = Caller();
        var calls = 0;
        feed.Changed += (_, _) =>
        {
            seen = CallerContext.Current;
            calls++;
        };
        using (CallerContext.Use(Caller()))
        {
            feed.Publish(new DeviceChange(DeviceChangeKind.Updated, Guid.NewGuid(), new Device { Serial = "ACCC8E000001" }));
            Assert.Null(seen);
            seen = Caller();
            feed.PublishMany([new DeviceChange(DeviceChangeKind.Updated, Guid.NewGuid(), new Device { Serial = "ACCC8E000002" })]);
            Assert.Null(seen);
        }

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CorePluginsStartedOrStoppedInAnAdministratorsCallNeverSeeTheCaller()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider());
        var plugin = new CallerProbe();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin("test.probe", "1.0.0", null)));

        // Turned on, off and on again in a call (Plugins page): start, stop and the work the plugin starts see no user.
        using (CallerContext.Use(Caller()))
        {
            await host.StartAllAsync(CancellationToken.None);
            registry.SetEnabled("test.probe", false);
            await host.SyncEnabledAsync(CancellationToken.None);
            registry.SetEnabled("test.probe", true);
            await host.SyncEnabledAsync(CancellationToken.None);
            await host.StopAllAsync(CancellationToken.None);
            Assert.NotNull(CallerContext.Current);
        }

        Assert.Equal(["start", "loop", "stop", "start", "loop", "stop"], plugin.Seen.Select(s => s.Step));
        Assert.All(plugin.Seen, s => Assert.Null(s.Caller));
    }

    /// <summary>Records the caller at its start and stop, and in the work its start begins (a loop, a timer).</summary>
    private sealed class CallerProbe : Sdk.Plugins.ICorePlugin
    {
        public List<(string Step, AuthenticatedCaller? Caller)> Seen { get; } = [];

        public string Id => "test.probe";

        public string DisplayName => "Probe";

        public string? IconKey => null;

        public IReadOnlyList<Sdk.Plugins.ITaskPlugin> TaskPlugins => [];

        public async Task StartAsync(Sdk.Plugins.ICorePluginContext ctx, CancellationToken ct)
        {
            Seen.Add(("start", CallerContext.Current));
            var loop = await Task.Run(() => CallerContext.Current, ct);
            Seen.Add(("loop", loop));
        }

        public Task StopAsync(CancellationToken ct)
        {
            Seen.Add(("stop", CallerContext.Current));
            return Task.CompletedTask;
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
