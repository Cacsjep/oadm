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
        ValueFields = [new TriggerValue("port", "Port number", EventValueKind.Number), new TriggerValue("active", "Active", EventValueKind.YesNo)],
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

    /// <summary>False: the API list was not read yet (a device just added), <see cref="Apis"/> is empty.</summary>
    public bool ApisRead { get; init; } = true;

    public string Serial => "ACCC8E000001";

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => !ApisRead ? [] : HasApi ? [new DeviceApi(DeviceTriggersPluginInfo.ApiId, "1.0"), new DeviceApi("param-cgi", "1.0")] : [new DeviceApi("param-cgi", "1.0")];
}

internal sealed class FakeDevices : IDeviceRepository
{
    private readonly List<IDeviceInfo> _items = [];
    private TaskCompletionSource? _hold;
    private TaskCompletionSource? _held;

    /// <summary>
    /// The next <see cref="ListAsync"/> waits (ignoring its token, like a slow database) until the returned source is set;
    /// <paramref name="held"/> completes once a call waits.
    /// </summary>
    public TaskCompletionSource HoldNextList(out Task held)
    {
        _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held = _held.Task;
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _hold;
    }

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

    public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<IDeviceInfo> items;
        lock (_items)
        {
            items = [.. _items];
        }

        if (Interlocked.Exchange(ref _hold, null) is { } hold)
        {
            _held?.TrySetResult();
            await hold.Task;
        }

        return items;
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

#pragma warning disable SA1401, CA1051 // Interlocked counters.
    /// <summary>Event list reads in progress on all cameras, and the most at once.</summary>
    public int ReadsInFlight;

    public int MaxReadsInFlight;
#pragma warning restore SA1401, CA1051

    /// <summary>While set, every event list read of every camera waits for it.</summary>
    public Task? HoldReads { get; set; }

    public FakeCamera this[Guid id] => _cameras.GetOrAdd(id, _ => new FakeCamera(this));

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(this[deviceId]);
}

/// <summary>
/// A camera with the recorded event list and the WebSocket event stream as AXIS OS 12.11 behaves: a filter is answered
/// with <c>data: {}</c>, then the current state of every matching on/off event, then live events. Read-only.
/// </summary>
internal sealed class FakeCamera(FakeCameras? owner = null) : IVapixClient
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (string Topic, Dictionary<string, string> Source, Dictionary<string, string> Data)> _states = [];
    private Connection? _current;

    public string Declarations { get; set; } = Fixture.Recorded;

    /// <summary>Connections fail with this while set (unreachable device).</summary>
    public Exception? Refuse { get; set; }

    public int Connections;

    /// <summary>Event list reads (GetEventInstances).</summary>
    public int Reads;

    /// <summary>A filter list with one of these topics is refused like AXIS OS does (error 2104).</summary>
    public ConcurrentDictionary<string, bool> RefusedTopics { get; } = new(StringComparer.Ordinal);

    /// <summary>While set, event list reads of this camera wait for it.</summary>
    public Task? HoldReads { get; set; }

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
        Interlocked.Increment(ref Reads);
        if (owner is not null)
        {
            var now = Interlocked.Increment(ref owner.ReadsInFlight);
            int max;
            while (now > (max = Volatile.Read(ref owner.MaxReadsInFlight)) && Interlocked.CompareExchange(ref owner.MaxReadsInFlight, now, max) != max)
            {
            }
        }

        try
        {
            if ((HoldReads ?? owner?.HoldReads) is { } hold)
            {
                await hold.WaitAsync(ct);
            }
        }
        finally
        {
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner.ReadsInFlight);
            }
        }

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
                    var request = JsonNode.Parse(text)!;
                    var context = request["context"]?.GetValue<string>() ?? string.Empty;
                    var filters = request["params"]!["eventFilterList"]!.AsArray();
                    var topics = filters.Select(f => f!["topicFilter"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    if (topics.Any(camera.RefusedTopics.ContainsKey))
                    {
                        _topics = [];
                        await SendAsync($$$"""{"apiVersion":"1.0","context":"{{{context}}}","method":"events:configure","error":{"code":2104,"message":"Could not use supplied event filter"}}""");
                        continue;
                    }

                    _topics = topics;
                    await SendAsync($$$"""{"apiVersion":"1.0","context":"{{{context}}}","method":"events:configure","data":{}}""");
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

    /// <summary>The same calls with the full type ids ("replace oadm.device-triggers.&lt;id&gt; []").</summary>
    public ConcurrentQueue<string> Full { get; } = new();

    public List<string> Lines => [.. Calls];

    public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null)
    {
        Full.Enqueue($"set {typeId} {deviceId} {(isOn ? "on" : "off")}");
        Calls.Enqueue($"set {Short(typeId)} {deviceId} {(isOn ? "on" : "off")}{Data(data)}");
    }

    public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null)
    {
        Full.Enqueue($"fire {typeId} {deviceId}");
        Calls.Enqueue($"fire {Short(typeId)} {deviceId}{Data(data)}");
    }

    public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices)
    {
        Full.Enqueue($"replace {typeId} [{string.Join(",", onDevices)}]");
        Calls.Enqueue($"replace {Short(typeId)} [{string.Join(",", onDevices)}]");
    }

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
    public Rig(DeviceTriggersOptions? options = null, TimeSpan? lostDelay = null, int? maxWatched = null)
    {
        var fast = FastOptions();
        Plugin = new DeviceTriggersPlugin(options ?? new DeviceTriggersOptions
        {
            LostDelay = lostDelay ?? fast.LostDelay,
            SettleTime = fast.SettleTime,
            RequestTimeout = fast.RequestTimeout,
            KeepAlive = fast.KeepAlive,
            Backoff = fast.Backoff,
            StatusRetry = fast.StatusRetry,
            TickInterval = fast.TickInterval,
            DeviceSweepInterval = fast.DeviceSweepInterval,
            RecheckInterval = fast.RecheckInterval,
            ResolveInterval = fast.ResolveInterval,
            MaxWatchedDevices = maxWatched ?? DeviceTriggersPluginInfo.MaxWatchedDevices,
        });
    }

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeCameras Cameras { get; } = new();

    public RecordingPublisher Publisher { get; } = new();

    public MemorySettings Settings { get; } = new();

    public DeviceTriggersPlugin Plugin { get; }

    public int TypesChanged;

    /// <summary>The context the plugin runs with (tests that drive the runtime directly use it too).</summary>
    public ICorePluginContext Context => _context ??= new FakeContext(Devices, Cameras, Hub.For(DeviceTriggersPluginInfo.PluginId), Publisher, Settings);

    private ICorePluginContext? _context;

    /// <summary>Short timings for a runtime driven directly.</summary>
    public static DeviceTriggersOptions FastOptions(int connectParallelism = 16, TimeSpan? sweep = null) => new()
    {
        LostDelay = TimeSpan.FromMilliseconds(400),
        // The fake camera sends its states right after the filter reply; what arrives within this time is its full report.
        // Generous, so a stalled thread on a busy machine never splits the report (the exact report lines are tested).
        SettleTime = TimeSpan.FromSeconds(3),
        RequestTimeout = TimeSpan.FromSeconds(5),
        KeepAlive = TimeSpan.FromSeconds(10),
        Backoff = [TimeSpan.FromMilliseconds(50)],
        StatusRetry = TimeSpan.FromMilliseconds(100),
        TickInterval = TimeSpan.FromMilliseconds(20),
        DeviceSweepInterval = sweep ?? TimeSpan.FromMilliseconds(100),
        RecheckInterval = TimeSpan.FromMinutes(5),
        ResolveInterval = TimeSpan.FromMilliseconds(100),
        ConnectParallelism = connectParallelism,
    };

    public async Task<Rig> StartAsync()
    {
        Plugin.EventTypesChanged += (_, _) => Interlocked.Increment(ref TypesChanged);
        await Plugin.StartAsync(Context, CancellationToken.None);
        return this;
    }

    public TestDevice AddDevice(string address, bool hasApi = true, DeviceStatus status = DeviceStatus.Ok, IReadOnlyList<string>? tags = null, bool apisRead = true)
    {
        var device = new TestDevice(address, HasApi: hasApi, Status: status) { Tags = tags ?? [], ApisRead = apisRead };
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
    public static Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 30_000) =>
        Oadm.Sdk.Testing.Eventually.TrueAsync(condition, what, TimeSpan.FromMilliseconds(timeoutMs));
}
