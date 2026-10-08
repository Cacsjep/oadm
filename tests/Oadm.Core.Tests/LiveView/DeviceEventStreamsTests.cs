using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.LiveView;

/// <summary>The server's <see cref="IDeviceEventStreams"/> (stored credentials, never handed to the plugin) and its way into the core plugin context.</summary>
public sealed class DeviceEventStreamsTests
{
    [Fact]
    public async Task OpensTheEventStreamWithTheStoredCredentials()
    {
        await using var server = new FakeEventRtspServer();
        await using var db = await TestDatabase.CreateAsync();
        var device = await db.Get<DeviceRepository>().AddAsync(new Device { Serial = "B8A44F000001", Address = "127.0.0.1" }, CancellationToken.None);
        await db.Get<CredentialStore>().SetAsync(device.Id, "root", "secret", CancellationToken.None);
        var streams = new DeviceEventStreams(db.Get<DeviceRepository>(), db.Get<CredentialStore>()) { RtspPort = server.Port };

        await using var source = await streams.OpenAsync(device.Id, CancellationToken.None);
        var count = 0;
        await foreach (var unused in source.ReadAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(129, count);
    }

    [Fact]
    public async Task UnknownDeviceIsNotFound()
    {
        await using var db = await TestDatabase.CreateAsync();
        var streams = new DeviceEventStreams(db.Get<DeviceRepository>(), db.Get<CredentialStore>());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => streams.OpenAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task CorePluginContextCarriesTheEventStreams()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        var streams = new NoStreams();
        var plugin = new ContextCapture();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin("test", "1.0.0", null)));

        await using (var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(), eventStreams: streams))
        {
            await host.StartAllAsync(CancellationToken.None);
            Assert.Same(streams, plugin.Context!.EventStreams);
            await host.StopAllAsync(CancellationToken.None);
        }

        await using var without = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider());
        await without.StartAllAsync(CancellationToken.None);
        Assert.Null(plugin.Context!.EventStreams);
    }

    private sealed class NoStreams : IDeviceEventStreams
    {
        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ContextCapture : ICorePlugin
    {
        public string Id => "core.capture";

        public string DisplayName => "Capture";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
