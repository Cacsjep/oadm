using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Core.LiveView.Rtp;
using Oadm.Core.Plugins;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>The event stream recorded read-only from 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11), as XML documents.</summary>
internal static class RecordedEvents
{
    private static readonly Lazy<IReadOnlyList<string>> Loaded = new(Load);

    public static IReadOnlyList<string> Documents => Loaded.Value;

    private static List<string> Load()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "events.rtp"));
        var depacketizer = new MetadataDepacketizer();
        var documents = new List<string>();
        for (var i = 0; i + 2 <= file.Length;)
        {
            var length = (file[i] << 8) | file[i + 1];
            i += 2;
            Assert.True(RtpPacket.TryParse(file.AsMemory(i, length), out var packet));
            i += length;
            if (depacketizer.Push(packet) is { } document)
            {
                documents.Add(document.Xml);
            }
        }

        return documents;
    }

    /// <summary>A document with one notification (like the device sends) for generated tests.</summary>
    public static string Notification(string topic, string operation, int port, int active, DateTime? utc = null) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <tt:MetadataStream xmlns:tt="http://www.onvif.org/ver10/schema">
        <tt:Event><wsnt:NotificationMessage xmlns:tns1="http://www.onvif.org/ver10/topics" xmlns:tnsaxis="http://www.axis.com/2009/event/topics" xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"><wsnt:Topic Dialect="http://docs.oasis-open.org/wsn/t-1/TopicExpression/Simple">{topic}</wsnt:Topic><wsnt:Message><tt:Message UtcTime="{(utc ?? new DateTime(2026, 10, 8, 9, 30, 0, DateTimeKind.Utc)):yyyy-MM-ddTHH:mm:ss.ffffffZ}" PropertyOperation="{operation}"><tt:Source><tt:SimpleItem Name="port" Value="{port}"/></tt:Source><tt:Key></tt:Key><tt:Data><tt:SimpleItem Name="active" Value="{active}"/></tt:Data></tt:Message></wsnt:Message></wsnt:NotificationMessage></tt:Event></tt:MetadataStream>
        """;
}

/// <summary>A scripted event source: the test writes documents, breaks or ends it; records disposal (TEARDOWN).</summary>
internal sealed class FakeEventSource : IDeviceEventSource
{
    private readonly Channel<object> _items = Channel.CreateUnbounded<object>();

    public bool Disposed { get; private set; }

    public int LostDocuments { get; set; }

    public void Write(string xml) => _items.Writer.TryWrite(new DeviceMetadataDocument(xml, new DateTimeOffset(2026, 10, 8, 9, 30, 1, TimeSpan.Zero)));

    public void Break(string message = "Unreachable - the connection was lost", DeviceStreamError error = DeviceStreamError.Unreachable) =>
        _items.Writer.TryWrite(new DeviceStreamException(error, message));

    public void End() => _items.Writer.TryComplete();

    public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _items.Reader.WaitToReadAsync(ct))
        {
            while (_items.Reader.TryRead(out var item))
            {
                if (item is Exception ex)
                {
                    throw ex;
                }

                yield return (DeviceMetadataDocument)item;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _items.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens <see cref="FakeEventSource"/>s, or throws what <see cref="Refuse"/> queued.</summary>
internal sealed class FakeEventStreams : IDeviceEventStreams
{
    private readonly Queue<Exception> _refusals = new();
    private readonly Lock _gate = new();

    public List<FakeEventSource> Opened { get; } = [];

    /// <summary>Stored RTSP ports (null = the host has no setting: SetRtspPortAsync throws like the SDK default).</summary>
    public Dictionary<Guid, int>? Ports { get; set; }

    public Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct) =>
        Task.FromResult(Ports is not null && Ports.TryGetValue(deviceId, out var port) ? port : (int?)null);

    public Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct)
    {
        if (Ports is null)
        {
            throw new NotSupportedException();
        }

        if (port is null or 554)
        {
            Ports.Remove(deviceId);
        }
        else
        {
            Ports[deviceId] = port.Value;
        }

        return Task.CompletedTask;
    }

    public int Attempts { get; private set; }

    public FakeEventSource? Last
    {
        get
        {
            lock (_gate)
            {
                return Opened.Count == 0 ? null : Opened[^1];
            }
        }
    }

    public void Refuse(DeviceStreamError error, string message)
    {
        lock (_gate)
        {
            _refusals.Enqueue(new DeviceStreamException(error, message));
        }
    }

    public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        lock (_gate)
        {
            Attempts++;
            if (_refusals.TryDequeue(out var refusal))
            {
                throw refusal;
            }

            var source = new FakeEventSource();
            Opened.Add(source);
            return Task.FromResult<IDeviceEventSource>(source);
        }
    }
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

internal sealed record TestDevice(string Address, string? Model, DeviceStatus Status = DeviceStatus.Ok, DeviceCategory Category = DeviceCategory.Camera) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial => "B8A44F" + (Address.GetHashCode(StringComparison.Ordinal) & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];
}

/// <summary>Core plugin context: fake devices, the real event hub, fake event streams.</summary>
internal sealed class TestCoreContext(FakeDevices devices, IPluginEvents? events, IDeviceEventStreams? streams) : ICorePluginContext
{
    public IDeviceRepository Devices { get; } = devices;

    public IVapixClientFactory Vapix => throw new NotSupportedException();

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings => throw new NotSupportedException();

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events { get; } = events;

    public IDeviceEventStreams? EventStreams { get; } = streams;
}

/// <summary>The page context against the in-process plugin (InvokeAsync) and the real event hub (WatchEventsAsync).</summary>
internal sealed class PluginPageContext(MetadataMonitorPlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public event EventHandler? DevicesChanged;

    public List<string> Calls { get; } = [];

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        Calls.Add(method);
        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(MetadataMonitorPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Plugin, hub, devices and fake streams wired together with fast timings.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public Rig(MetadataMonitorOptions? options = null)
    {
        Plugin = new MetadataMonitorPlugin(options ?? Fast());
        Camera = new TestDevice("10.0.0.48", "P3265-V");
        Devices.Items.Add(Camera);
    }

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeEventStreams Streams { get; } = new();

    public MetadataMonitorPlugin Plugin { get; }

    public TestDevice Camera { get; }

    public static MetadataMonitorOptions Fast() => new()
    {
        BatchInterval = TimeSpan.FromMilliseconds(20),
        ReconnectDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)],
        // Long lease: these tests send no keep-alive, and a slow CI runner (macOS) took longer than a short lease, so the
        // server ended the stream as designed and the test waited for a reconnect. ShortLease() is for the lease test.
        LeaseTimeout = TimeSpan.FromMinutes(1),
        LeaseCheckInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary><see cref="Fast"/> with a 1 s lease: for the test that a stream without keep-alive ends.</summary>
    public static MetadataMonitorOptions ShortLease() => new()
    {
        BatchInterval = TimeSpan.FromMilliseconds(20),
        ReconnectDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)],
        LeaseTimeout = TimeSpan.FromSeconds(1),
        LeaseCheckInterval = TimeSpan.FromMilliseconds(50),
    };

    public async Task<Rig> StartAsync(bool withStreams = true)
    {
        await Plugin.StartAsync(new TestCoreContext(Devices, Hub.For(MetadataMonitorPluginInfo.PluginId), withStreams ? Streams : null), CancellationToken.None);
        return this;
    }

    public async Task<StartReply> StartStreamAsync(Guid? deviceId = null) =>
        MetadataJson.Deserialize<StartReply>(await Plugin.InvokeAsync(MetadataMethods.Start, MetadataJson.Serialize(new StartRequest(deviceId ?? Camera.Id)), CancellationToken.None));

    public Task StopStreamAsync(string streamId) =>
        Plugin.InvokeAsync(MetadataMethods.Stop, MetadataJson.Serialize(new StreamRequest(streamId)), CancellationToken.None);

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

/// <summary>Collects the plugin's events from the hub.</summary>
internal sealed class EventLog : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;
    private readonly Lock _gate = new();
    private readonly List<PluginEvent> _events = [];

    public EventLog(PluginEventHub hub)
    {
        var subscribed = new TaskCompletionSource();
        _run = Task.Run(async () =>
        {
            var enumerator = hub.WatchAsync(MetadataMonitorPluginInfo.PluginId, _cts.Token).GetAsyncEnumerator(_cts.Token);
            var next = enumerator.MoveNextAsync();
            subscribed.SetResult();
            try
            {
                while (await next)
                {
                    lock (_gate)
                    {
                        _events.Add(enumerator.Current);
                    }

                    next = enumerator.MoveNextAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        subscribed.Task.Wait();
        Wait.UntilAsync(() => hub.WatcherCount(MetadataMonitorPluginInfo.PluginId) > 0).GetAwaiter().GetResult();
    }

    public List<MonitorState> States
    {
        get
        {
            lock (_gate)
            {
                return [.. _events.Where(e => e.Topic == MetadataMethods.StateTopic).Select(e => MetadataJson.Deserialize<MonitorState>(e.PayloadJson))];
            }
        }
    }

    public List<MessagesEvent> Batches
    {
        get
        {
            lock (_gate)
            {
                return [.. _events.Where(e => e.Topic == MetadataMethods.MessagesTopic).Select(e => MetadataJson.Deserialize<MessagesEvent>(e.PayloadJson))];
            }
        }
    }

    public List<MetadataMessage> Messages => [.. Batches.SelectMany(b => b.Messages)];

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _run;
        _cts.Dispose();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        // Generous: the condition usually holds within milliseconds; a busy CI runner (ubuntu, v1.0.0) needed more than 5 s.
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
