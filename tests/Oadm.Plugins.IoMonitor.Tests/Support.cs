using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.IoMonitor.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.IoMonitor.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
    public static Window Host(Control view)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(IoMonitorPluginInfo.PluginId, IoMonitorPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1300, Height = 660, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static void Capture(Window window, string name)
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

internal sealed record TestDevice(string Address, string? Model, bool HasIo = true, DeviceStatus Status = DeviceStatus.Ok) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial => "ACCC8E000001";

    public string? HostName => null;

    public string? FirmwareVersion => "12.10.73";

    public DeviceCategory Category => DeviceCategory.IoModule;

    public bool HasVideo => false;

    public IReadOnlyList<DeviceApi> Apis => HasIo ? [new DeviceApi(IoMonitorPluginInfo.ApiId, "1.1")] : [];
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

/// <summary>
/// A device answering getPorts with <see cref="Body"/> (the recorded A9210 by default); read-only. Like the host's factory
/// it caches one client and <see cref="ReplaceClient"/> disposes it and hands out a new one (address or credentials changed).
/// </summary>
internal sealed class FakeVapix : IVapixClientFactory
{
    private Client _client;

    public FakeVapix()
    {
        _client = new Client(this);
    }

    public static string Recorded => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "a9210-getports.json"));

    public string Body { get; set; } = Recorded;

    public Func<HttpResponseMessage>? Override { get; set; }

    public int Requests;

    /// <summary>Calls of <see cref="CreateAsync"/>.</summary>
    public int Creates;

    /// <summary>Thrown by <see cref="CreateAsync"/> (device removed, unknown) while set.</summary>
    public Exception? CreateError { get; set; }

    /// <summary>The factory replaced its client: the old one is disposed and fails every request.</summary>
    public void ReplaceClient()
    {
        var old = _client;
        _client = new Client(this);
        old.Disposed = true;
    }

    /// <summary>The recorded ports with some ports active (state "closed").</summary>
    public void SetClosed(params string[] ports)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Recorded)!;
        foreach (var item in doc["data"]!["items"]!.AsArray())
        {
            item!["state"] = ports.Contains(item["port"]!.ToString()) ? "closed" : "open";
        }

        Body = doc.ToJsonString();
    }

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        Interlocked.Increment(ref Creates);
        return CreateError is { } error ? Task.FromException<IVapixClient>(error) : Task.FromResult<IVapixClient>(_client);
    }

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        Assert.Equal(IoMonitorPluginInfo.Path, request.RequestUri!.OriginalString);
        Assert.Contains("\"getPorts\"", await request.Content!.ReadAsStringAsync(ct), StringComparison.Ordinal); // read-only
        return Override?.Invoke() ?? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }

    private sealed class Client(FakeVapix device) : IVapixClient
    {
        public volatile bool Disposed;

        public Uri BaseAddress => new("http://device/");

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Disposed ? throw new ObjectDisposedException(nameof(HttpClient)) : device.AnswerAsync(request, ct);

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

internal sealed class FakeContext(IDeviceRepository devices, IVapixClientFactory vapix, IPluginEvents events) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => vapix;

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings => throw new NotSupportedException();

    public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events => events;

    public IDeviceEventStreams? EventStreams => null;

    public IFirewallRules? Firewall => null;
}

/// <summary>The page context against the plugin in process (invoke + the event hub).</summary>
internal sealed class PluginPageContext(IoMonitorPlugin plugin, PluginEventHub hub, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public IReadOnlyList<IDeviceInfo> Devices { get; } = devices;

    /// <summary>Calls of this method fail (a dropped connection) while set.</summary>
    public string? FailMethod { get; set; }

    /// <summary>Runs with the new stream id before the Start reply returns (an event that overtakes the reply).</summary>
    public Action<string>? BeforeStartReply { get; set; }

    /// <summary>False: the page gets no pushed events at all.</summary>
    public bool Watch { get; set; } = true;

    public event EventHandler? DevicesChanged;

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        Calls.Enqueue(method);
        if (method == FailMethod)
        {
            throw new IOException("The connection to the server was lost.");
        }

        var reply = await plugin.InvokeAsync(method, payloadJson, ct);
        if (method == IoMonitorMethods.Start && BeforeStartReply is { } before
            && IoMonitorJson.Deserialize<StartReply>(reply).StreamId is { } streamId)
        {
            before(streamId);
        }

        return reply;
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Watch)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        await foreach (var item in hub.WatchAsync(IoMonitorPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }
}

internal sealed class Rig : IAsyncDisposable
{
    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeVapix Vapix { get; } = new();

    /// <param name="leaseTimeout">Without keep-alives a stream stops after this (default 1 minute, far beyond a stalled runner; the lease tests shorten it).</param>
    /// <param name="retryDelay">Wait after a failed read (default 50 ms).</param>
    /// <param name="time">Clock of the plugin (default the system clock).</param>
    public Rig(TimeSpan? leaseTimeout = null, TimeSpan? retryDelay = null, TimeProvider? time = null)
    {
        Plugin = new(
            new IoMonitorOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
                RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50),
                LeaseTimeout = leaseTimeout ?? TimeSpan.FromMinutes(1),
                LeaseCheckInterval = TimeSpan.FromMilliseconds(50),
                MaxStreams = 2,
            },
            time);
    }

    public IoMonitorPlugin Plugin { get; }

    public async Task<Rig> StartAsync()
    {
        await Plugin.StartAsync(new FakeContext(Devices, Vapix, Hub.For(IoMonitorPluginInfo.PluginId)), CancellationToken.None);
        return this;
    }

    public async Task<StartReply> StartStreamAsync(Guid deviceId) =>
        IoMonitorJson.Deserialize<StartReply>(await Plugin.InvokeAsync(IoMonitorMethods.Start, IoMonitorJson.Serialize(new StartRequest(deviceId)), CancellationToken.None));

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

internal static class Wait
{
    public static Task UntilAsync(Func<bool> condition) => Oadm.Sdk.Testing.Eventually.TrueAsync(condition, "the condition");
}

/// <summary>
/// A fake clock that knows how many waits (one-shot timers such as Task.Delay) are pending: the reading of a device waits
/// on one between two reads, so a test can move the clock exactly when the reading waits.
/// </summary>
internal sealed class CountingTimeProvider : Microsoft.Extensions.Time.Testing.FakeTimeProvider
{
    private int _waiting;

    /// <summary>One-shot timers that neither fired nor were disposed.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
        {
            return base.CreateTimer(callback, state, dueTime, period); // the lease check: periodic, never a read wait
        }

        var timer = new OneShot(this);
        Interlocked.Increment(ref _waiting);
        timer.Inner = base.CreateTimer(
            s =>
            {
                timer.End();
                callback(s);
            },
            state,
            dueTime,
            period);
        return timer;
    }

    /// <summary>
    /// Moves the clock by <paramref name="interval"/> once the reading waits, so it reads exactly once more, then waits
    /// (real time) until <paramref name="condition"/> holds. The clock moves only this once: a consumer on another thread
    /// that is slow to see the read never lets the clock run on (a test that stepped it on each wait could pass the lease
    /// and the stream ended under the page).
    /// </summary>
    public async Task ReadOnceAsync(Func<bool> condition, TimeSpan interval)
    {
        await Wait.UntilAsync(() => Waiting > 0);
        Advance(interval);
        await Wait.UntilAsync(condition);
    }

    private sealed class OneShot(CountingTimeProvider owner) : ITimer
    {
        private int _ended;

        public ITimer? Inner { get; set; }

        public void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Interlocked.Decrement(ref owner._waiting);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => Inner!.Change(dueTime, period);

        public void Dispose()
        {
            End();
            Inner?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            End();
            return Inner?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }
}
