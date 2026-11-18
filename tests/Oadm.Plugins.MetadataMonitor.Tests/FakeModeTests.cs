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

        // Stop first: without a UI thread the events arrive on the thread pool, so read the list once it is final: the
        // stream's last event (state Stopped, with the count it sent) was handled.
        await vm.StartStopCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.StatusText.StartsWith("Stopped", StringComparison.Ordinal));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => ctx.StoppedWithCount is not null, "the stream's end to be handled");
        var messages = vm.Messages.ToList();
        Assert.Equal(ctx.StoppedWithCount, messages.Count);
        Assert.Equal(64, messages.Count(m => m.Topic == "Device/IO/VirtualInput" && m.Operation == "Initialized"));
        Assert.Contains(messages, m => m.Topic == "Storage/Alert" && m.Info.StartsWith("[INIT] disk_id = SD_DISK;", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Topic == "Device/Status/Temperature/Inside");
        Assert.Contains(messages, m => m.Operation == "Changed" && m.Info.StartsWith("[CHANGED] port = ", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, messages.Count).Select(i => (long)i), messages.Select(m => m.Seq));
        Assert.All(messages, m => Assert.StartsWith("<wsnt:NotificationMessage", m.Xml, StringComparison.Ordinal));

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

        /// <summary>The message count of the stream's Stopped state, set once the page handled that event (its last).</summary>
        public int? StoppedWithCount => Volatile.Read(ref _stoppedWithCount) is var count and >= 0 ? count : null;

        private int _stoppedWithCount = -1;

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await using var items = api.WatchCorePluginAsync(MetadataMonitorPluginInfo.PluginId, ct).GetAsyncEnumerator(ct);
            var next = items.MoveNextAsync(); // runs up to the first wait: the subscription is made
            Subscribed = true;
            while (await next)
            {
                var item = items.Current;
                yield return new PluginEvent(item.Topic, item.PayloadJson);

                // Asked for the next event: the page handled this one (the watch handles one event at a time).
                if (item.Topic == MetadataMethods.StateTopic
                    && System.Text.Json.Nodes.JsonNode.Parse(item.PayloadJson) is { } state
                    && (string?)state["state"] == MonitorStates.Stopped)
                {
                    Volatile.Write(ref _stoppedWithCount, (int)state["messages"]!);
                }

                next = items.MoveNextAsync();
            }
        }
    }
}
