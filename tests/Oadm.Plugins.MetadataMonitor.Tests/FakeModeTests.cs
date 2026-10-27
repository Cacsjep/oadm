using System.Runtime.CompilerServices;

using Oadm.Client.Api;
using Oadm.Plugins.MetadataMonitor.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>The client's fake backend (<c>--fake</c>) speaks the plugin's JSON; the page works against it.</summary>
public sealed class FakeModeTests : IDisposable
{
    private readonly FakeOadmApi _api = new(TimeSpan.FromMilliseconds(5)) { MetadataChangeInterval = TimeSpan.FromMilliseconds(20) };

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Fake_backend_streams_generated_events_the_page_shows()
    {
        Assert.DoesNotContain(await _api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == MetadataMonitorPluginInfo.PluginId); // off by default
        await _api.SetPluginPackageEnabledAsync(MetadataMonitorPluginInfo.PluginId, true, CancellationToken.None);
        Assert.Contains(await _api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == MetadataMonitorPluginInfo.PluginId && p.IconKey == MetadataMonitorPluginInfo.IconKey);
        var device = (await _api.ListDevicesAsync(CancellationToken.None)).First(d => d.Address == "10.0.0.48");
        var ctx = new FakeContext(_api, new TestDevice(device.Address, "P3265-V") { Id = Guid.Parse(device.Id) });
        using var vm = new MetadataMonitorViewModel(ctx, new MetadataClientSettingsStore(Path.Combine(Path.GetTempPath(), "oadm-mm-tests", Guid.NewGuid().ToString("N"), "client.json")));
        vm.Activate();
        await Wait.UntilAsync(() => ctx.Subscribed); // the watch subscribes
        vm.SelectedCamera = CameraOption.Of(ctx.Devices.Single());

        await vm.StartStopCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.Messages.Count > 80);
        Assert.True(vm.IsRunning);
        await Wait.UntilAsync(() => vm.StatusText.StartsWith("Live", StringComparison.Ordinal));

        // Stop first: without a UI thread the events arrive on the thread pool, so read the list once it is final.
        await vm.StartStopCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.StatusText.StartsWith("Stopped", StringComparison.Ordinal));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(
            async () =>
            {
                var count = vm.Messages.Count;
                await Task.Delay(100); // a batch already under way when Stop was sent may still arrive
                return vm.Messages.Count == count;
            },
            "the list to be final");
        Assert.Equal(64, vm.Messages.Count(m => m.Topic == "Device/IO/VirtualInput" && m.Operation == "Initialized"));
        Assert.Contains(vm.Messages, m => m.Topic == "Storage/Alert" && m.Info.StartsWith("[INIT] disk_id = SD_DISK;", StringComparison.Ordinal));
        Assert.Contains(vm.Messages, m => m.Topic == "Device/Status/Temperature/Inside");
        Assert.Contains(vm.Messages, m => m.Operation == "Changed" && m.Info.StartsWith("[CHANGED] port = ", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, vm.Messages.Count).Select(i => (long)i), vm.Messages.Select(m => m.Seq));
        Assert.All(vm.Messages, m => Assert.StartsWith("<wsnt:NotificationMessage", m.Xml, StringComparison.Ordinal));

        vm.Deactivate();
    }

    [Fact]
    public async Task Fake_backend_refuses_devices_with_a_bad_status()
    {
        var device = (await _api.ListDevicesAsync(CancellationToken.None)).First(d => d.Status == Oadm.Contracts.V1.DeviceStatus.CredentialsRequired);
        var reply = MetadataJson.Deserialize<StartReply>(await _api.InvokeCorePluginAsync(MetadataMonitorPluginInfo.PluginId, MetadataMethods.Start, MetadataJson.Serialize(new StartRequest(Guid.Parse(device.Id))), CancellationToken.None));

        Assert.Null(reply.StreamId);
        Assert.Equal(DeviceMessages.CredentialsRequired, reply.Error);
    }

    private sealed class FakeContext(FakeOadmApi api, IDeviceInfo device) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = [device];

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(MetadataMonitorPluginInfo.PluginId, method, payloadJson, ct);

        /// <summary>True once the watch is subscribed to the fake backend.</summary>
        public bool Subscribed { get; private set; }

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await using var items = api.WatchCorePluginAsync(MetadataMonitorPluginInfo.PluginId, ct).GetAsyncEnumerator(ct);
            var next = items.MoveNextAsync(); // runs up to the first wait: the subscription is made
            Subscribed = true;
            while (await next)
            {
                yield return new PluginEvent(items.Current.Topic, items.Current.PayloadJson);
                next = items.MoveNextAsync();
            }
        }
    }
}
