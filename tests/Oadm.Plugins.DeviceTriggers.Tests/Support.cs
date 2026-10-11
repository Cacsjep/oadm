using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Plugins.DeviceTriggers.Runtime;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.DeviceTriggers.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

internal static class Headless
{
    public static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static void Capture(TopLevel window, string name)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}

internal static class Fixture
{
    public static string Recorded => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "p3265-eventinstances.xml"));

    public const string VirtualInput = "tns1:Device/tnsaxis:IO/VirtualInput";
    public const string Tampering = "tns1:VideoSource/tnsaxis:Tampering";

    public static TriggerDefinition VirtualInputTrigger(string name, IEnumerable<Guid> devices, string? port = "3") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Topic = VirtualInput,
        EventName = "Device › Input ports › Virtual input",
        Stateful = true,
        StateItem = "active",
        Filters = port is null ? [] : [new TriggerFilter("port", port)],
        FilterText = port is null ? string.Empty : "Port number: " + port,
        Values = ["port", "active"],
        DeviceIds = [.. devices],
    };

    public static TriggerDefinition TamperingTrigger(string name, IEnumerable<Guid> devices) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Topic = Tampering,
        EventName = "Video source › Camera tampering",
        Stateful = false,
        Values = ["channel", "tampering"],
        DeviceIds = [.. devices],
    };
}

internal sealed record TestDevice(string Address, string? Model = "P3265-V", bool HasApi = true, DeviceStatus Status = DeviceStatus.Ok) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string Serial => "ACCC8E000001";

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => HasApi ? [new DeviceApi(DeviceTriggersPluginInfo.ApiId, "1.0"), new DeviceApi("param-cgi", "1.0")] : [new DeviceApi("param-cgi", "1.0")];
}

internal sealed class FakeDevices : IDeviceRepository
{
    private readonly List<IDeviceInfo> _items = [];

    public void Add(params IDeviceInfo[] devices)
    {
        lock (_items)
        {
            _items.AddRange(devices);
        }
    }

    public void Remove(Guid id)
    {
        lock (_items)
        {
            _items.RemoveAll(d => d.Id == id);
        }
    }

    public void Replace(IDeviceInfo device)
    {
        lock (_items)
        {
            _items.RemoveAll(d => d.Id == device.Id);
            _items.Add(device);
        }
    }

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
    {
        lock (_items)
        {
            return Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _items]);
        }
    }

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
    {
        lock (_items)
        {
            return Task.FromResult(_items.FirstOrDefault(d => d.Id == id));
        }
    }
}

/// <summary>Every device id gets its own <see cref="FakeCamera"/> (created on first use).</summary>
internal sealed class FakeCameras : IVapixClientFactory
{
    private readonly ConcurrentDictionary<Guid, FakeCamera> _cameras = new();

    public FakeCamera this[Guid id] => _cameras.GetOrAdd(id, _ => new FakeCamera());

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(this[deviceId]);
}

/// <summary>
/// A camera with the recorded event list and the WebSocket event stream as AXIS OS 12.11 behaves: a filter is answered
/// with <c>data: {}</c>, then the current state of every matching on/off event, then live events. Read-only.
/// </summary>
internal sealed class FakeCamera : IVapixClient
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (string Topic, Dictionary<string, string> Source, Dictionary<string, string> Data)> _states = [];
    private Connection? _current;

    public string Declarations { get; set; } = Fixture.Recorded;

    /// <summary>Connections fail with this while set (unreachable device).</summary>
    public Exception? Refuse { get; set; }

    public int Connections;

    public ConcurrentQueue<string> Configures { get; } = new();

    public Uri BaseAddress => new("http://camera/");

    public bool IsConnected => _current is { IsOpen: true };

    /// <summary>An on/off event changes (sent when the stream asks for its topic).</summary>
    public void SetState(string topic, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> data)
    {
        Connection? current;
        lock (_sync)
        {
            _states[topic + "|" + string.Join(",", source.OrderBy(s => s.Key).Select(s => s.Key + "=" + s.Value))] = (topic, new(source), new(data));
            current = _current;
        }

        current?.Send(topic, source, data);
    }

    /// <summary>A one-shot event happens.</summary>
    public void Fire(string topic, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> data)
    {
        Connection? current;
        lock (_sync)
        {
            current = _current;
        }

        current?.Send(topic, source, data);
    }

    /// <summary>Breaks the current connection (network gone).</summary>
    public void Drop()
    {
        Connection? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }

        current?.Abort();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("/vapix/services", request.RequestUri!.OriginalString);
        Assert.Contains("GetEventInstances", await request.Content!.ReadAsStringAsync(ct), StringComparison.Ordinal); // read-only
        if (Refuse is { } refuse)
        {
            throw refuse;
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Declarations, Encoding.UTF8, "application/soap+xml") };
    }

    public Task<WebSocket> ConnectWebSocketAsync(string pathAndQuery, TimeSpan keepAlive, CancellationToken ct)
    {
        Assert.Equal(DeviceTriggersPluginInfo.StreamPath, pathAndQuery);
        if (Refuse is { } refuse)
        {
            throw refuse;
        }

        Interlocked.Increment(ref Connections);
        var (client, server) = DuplexPipe.CreatePair();
        var connection = new Connection(this, WebSocket.CreateFromStream(server, new WebSocketCreationOptions { IsServer = true }));
        lock (_sync)
        {
            _current = connection;
        }

        connection.Start();
        return Task.FromResult(WebSocket.CreateFromStream(client, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan }));
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();

    private List<(string Topic, Dictionary<string, string> Source, Dictionary<string, string> Data)> StatesOf(IReadOnlyCollection<string> topics)
    {
        lock (_sync)
        {
            return [.. _states.Values.Where(s => topics.Contains(s.Topic))];
        }
    }

    private sealed class Connection(FakeCamera camera, WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private volatile HashSet<string> _topics = [];

        public bool IsOpen => socket.State == WebSocketState.Open;

        public void Start() => _ = Task.Run(LoopAsync);

        public void Abort() => socket.Abort();

        public void Send(string topic, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> data)
        {
            if (_topics.Contains(topic))
            {
                _ = SendAsync(Notify(topic, source, data));
            }
        }

        private static string Notify(string topic, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> data)
        {
            var src = new JsonObject();
            foreach (var (k, v) in source)
            {
                src[k] = v;
            }

            var dat = new JsonObject();
            foreach (var (k, v) in data)
            {
                dat[k] = v;
            }

            return new JsonObject
            {
                ["apiVersion"] = "1.0",
                ["method"] = "events:notify",
                ["params"] = new JsonObject
                {
                    ["notification"] = new JsonObject
                    {
                        ["topic"] = topic,
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["message"] = new JsonObject { ["source"] = src, ["key"] = new JsonObject(), ["data"] = dat },
                    },
                },
            }.ToJsonString();
        }

        private async Task SendAsync(string text)
        {
            await _send.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException)
            {
            }
            finally
            {
                _send.Release();
            }
        }

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    var text = await DeviceConnection.ReceiveTextAsync(socket, CancellationToken.None);
                    if (text is null)
                    {
                        return;
                    }

                    camera.Configures.Enqueue(text);
                    var filters = JsonNode.Parse(text)!["params"]!["eventFilterList"]!.AsArray();
                    _topics = [.. filters.Select(f => f!["topicFilter"]!.GetValue<string>())];
                    await SendAsync("""{"apiVersion":"1.0","context":"oadm","method":"events:configure","data":{}}""");
                    foreach (var (topic, source, data) in camera.StatesOf(_topics))
                    {
                        await SendAsync(Notify(topic, source, data));
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException)
            {
            }
        }
    }
}

/// <summary>Two connected in-memory streams (a WebSocket client and server without a network).</summary>
internal sealed class DuplexPipe(Stream reader, Stream writer) : Stream
{
    public static (Stream Client, Stream Server) CreatePair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        return (new DuplexPipe(toClient.Reader.AsStream(), toServer.Writer.AsStream()), new DuplexPipe(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => writer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reader.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => writer.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            reader.Dispose();
            writer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Records what the plugin publishes to the event rules, as text lines.</summary>
internal sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public List<string> Lines => [.. Calls];

    public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) =>
        Calls.Enqueue($"set {Short(typeId)} {deviceId} {(isOn ? "on" : "off")}{Data(data)}");

    public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null) =>
        Calls.Enqueue($"fire {Short(typeId)} {deviceId}{Data(data)}");

    public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices) =>
        Calls.Enqueue($"replace {Short(typeId)} [{string.Join(",", onDevices)}]");

    public bool IsWatched(string typeId) => true;

    /// <summary>"trigger" or "lost" instead of the long ids.</summary>
    private static string Short(string typeId) => typeId.EndsWith(".lost", StringComparison.Ordinal) ? "lost" : "trigger";

    private static string Data(IReadOnlyDictionary<string, string>? data) =>
        data is null ? string.Empty : " " + string.Join(",", data.OrderBy(d => d.Key).Select(d => d.Key + "=" + d.Value));
}

internal sealed class MemorySettings : IPluginSettings
{
    public ConcurrentDictionary<string, string?> Values { get; } = new();

    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        Values[key] = valueJson;
        return Task.CompletedTask;
    }
}

internal sealed class FakeContext(FakeDevices devices, FakeCameras cameras, IPluginEvents? events, IEventPublisher publisher, MemorySettings settings) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => cameras;

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings => settings;

    public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events => events;

    public IDeviceEventStreams? EventStreams => null;

    public IFirewallRules? Firewall => null;

    public IEventPublisher? EventPublisher => publisher;
}

/// <summary>The plugin with fake devices and cameras and short timings.</summary>
internal sealed class Rig : IAsyncDisposable
{
    /// <param name="lostDelay">"Connection lost" after this (default 400 ms; tests that look at the state before it use more).</param>
    public Rig(DeviceTriggersOptions? options = null, TimeSpan? lostDelay = null)
    {
        Plugin = new DeviceTriggersPlugin(options ?? new DeviceTriggersOptions
        {
            LostDelay = lostDelay ?? TimeSpan.FromMilliseconds(400),
            SettleTime = TimeSpan.FromMilliseconds(150),
            RequestTimeout = TimeSpan.FromSeconds(5),
            KeepAlive = TimeSpan.FromSeconds(10),
            Backoff = [TimeSpan.FromMilliseconds(50)],
            StatusRetry = TimeSpan.FromMilliseconds(100),
            TickInterval = TimeSpan.FromMilliseconds(20),
            DeviceSweepInterval = TimeSpan.FromMilliseconds(100),
            RecheckInterval = TimeSpan.FromMinutes(5),
            ResolveInterval = TimeSpan.FromMilliseconds(100),
        });
    }

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeCameras Cameras { get; } = new();

    public RecordingPublisher Publisher { get; } = new();

    public MemorySettings Settings { get; } = new();

    public DeviceTriggersPlugin Plugin { get; }

    public int TypesChanged;

    public async Task<Rig> StartAsync()
    {
        Plugin.EventTypesChanged += (_, _) => Interlocked.Increment(ref TypesChanged);
        await Plugin.StartAsync(new FakeContext(Devices, Cameras, Hub.For(DeviceTriggersPluginInfo.PluginId), Publisher, Settings), CancellationToken.None);
        return this;
    }

    public TestDevice AddDevice(string address, bool hasApi = true, DeviceStatus status = DeviceStatus.Ok, IReadOnlyList<string>? tags = null)
    {
        var device = new TestDevice(address, HasApi: hasApi, Status: status) { Tags = tags ?? [] };
        Devices.Add(device);
        return device;
    }

    public async Task<T> CallAsync<T>(string method, object? payload = null) =>
        TriggerJson.Deserialize<T>(await Plugin.InvokeAsync(method, payload is null ? null : TriggerJson.Serialize(payload), CancellationToken.None));

    public async Task<TriggerDefinition> SaveAsync(TriggerDefinition trigger)
    {
        var reply = await CallAsync<SaveReply>(DeviceTriggersMethods.Save, new SaveRequest(trigger));
        Assert.True(reply.Trigger is not null, reply.Error ?? string.Join("; ", reply.Errors ?? new Dictionary<string, string>()));
        return reply.Trigger!;
    }

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting: " + what);
            await Task.Delay(10);
        }
    }
}
