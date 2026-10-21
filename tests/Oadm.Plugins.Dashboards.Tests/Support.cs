using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Dashboards.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // OADM_TEST_APPEARANCE=Light renders the screenshots in the light appearance (colors and theme variant).
        var light = Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";
        Oadm.Theme.OadmColors.Current = light ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Dashboards.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    /// <summary>One headless application per test process (Avalonia sets up only once); never disposed.</summary>
    public static HeadlessUnitTestSession Session => Shared.Value;

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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(DashboardsPluginInfo.PluginId, DashboardsPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = 1500, Height = 820, Content = new Border { Padding = new Thickness(16), Child = page } };
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

    public static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (Dispatcher.UIThread.CheckAccess() && Application.Current is not null)
            {
                Pump();
            }

            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
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

internal sealed record TestDevice(string Address, string? Model, params string[] TagNames) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "ACCC8E" + Random.Shared.Next(0, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;

    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;

    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    public IReadOnlyList<DeviceApi> Apis => [];

    public IReadOnlyList<string> Tags => TagNames;

    public Guid? SiteId { get; init; }
}

/// <summary>The server's event feed: the types it knows and the events, handed to the handler at once.</summary>
internal sealed class FakeEventSubscriptions : IEventSubscriptions
{
    private readonly List<Action<PublishedEvent>> _handlers = [];

    public List<SubscribedEventType> TypeList { get; } = [];

    public IReadOnlyList<SubscribedEventType> Types => [.. TypeList];

    public int Subscribers => _handlers.Count;

    public event EventHandler? TypesChanged;

    public IDisposable Subscribe(Action<PublishedEvent> handler)
    {
        _handlers.Add(handler);
        return new Unsubscriber(() => _handlers.Remove(handler));
    }

    public void Publish(string typeId, Guid? device, PublishedEventKind kind, params (string Name, string Value)[] data)
    {
        var e = new PublishedEvent(typeId, device, kind, data.ToDictionary(d => d.Name, d => d.Value), DateTimeOffset.UtcNow);
        foreach (var handler in _handlers.ToList())
        {
            handler(e);
        }
    }

    public void ChangeTypes(Action<List<SubscribedEventType>> change)
    {
        change(TypeList);
        TypesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Event types like the server's: a device trigger with a value, its "connection lost", OADM's own and a server event.</summary>
internal static class Types
{
    public const string Temperature = "oadm.device-triggers.temperature";
    public const string TemperatureLost = Temperature + ".lost";
    public const string Gate = "oadm.device-triggers.gate";
    public const string Plate = "oadm.device-triggers.plate";
    public const string Unreachable = "oadm.device.unreachable";
    public const string ServerFlag = "acme.flag";

    public static List<SubscribedEventType> All() =>
    [
        Trigger(Temperature, "Temperature", EventKind.Stateless, new EventField("temperature", "Temperature") { Kind = EventValueKind.Number, Filter = false }),
        Trigger(TemperatureLost, "Temperature: connection lost", EventKind.Stateful, new EventField("reason", "Reason") { Filter = false }),
        Trigger(Gate, "Gate open", EventKind.Stateful, new EventField("port", "Port number") { Kind = EventValueKind.Number, Filter = false }),
        Trigger(Plate, "Number plate", EventKind.Stateless, new EventField("plate", "Plate") { Filter = false }, new EventField("speed", "Speed") { Filter = false }),
        new(new EventTypeInfo(Unreachable, "Device unreachable", "Device status", EventKind.Stateful), "oadm.device", "Built in", true),
        new(new EventTypeInfo(ServerFlag, "Flag", "Acme", EventKind.Stateful, EventScope.Server), "acme", "Acme", true),
    ];

    /// <summary>The devices of the trigger "Gate open" (the others take any device).</summary>
    public static List<Guid> GateDevices { get; } = [];

    private static SubscribedEventType Trigger(string id, string name, EventKind kind, params EventField[] fields) =>
        new(new EventTypeInfo(id, name, "Device triggers", kind, EventScope.Device, fields) { DeviceIds = id == Gate ? GateDevices : null },
            DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true);
}

/// <summary>The server part with its settings, events, event feed and clock in memory.</summary>
internal sealed class PluginHost : IDisposable
{
    public PluginHost(int maxKeys = DashboardsPluginInfo.MaxKeys, bool withEvents = true)
    {
        Feed.TypeList.AddRange(Types.All());
        var ctx = Substitute.For<ICorePluginContext>();
        Context = ctx;
        ctx.Settings.Returns(Settings);
        ctx.Events.Returns(Events);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(withEvents ? Feed : null);
        Plugin = new DashboardsPlugin(Time, maxKeys);
        Plugin.StartAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public DashboardsPlugin Plugin { get; }

    /// <summary>The plugin's context (a substitute): tests set what <c>Devices</c> answers.</summary>
    public ICorePluginContext Context { get; }

    public FakeEventSubscriptions Feed { get; } = new();

    public MemorySettings Settings { get; } = new();

    public RecordingEvents Events { get; } = new();

    /// <summary>One push interval later (the timer runs its tick).</summary>
    public void Tick() => Time.Advance(DashboardsPluginInfo.PushInterval);

    public void Dispose() => Plugin.Dispose();
}

internal sealed class MemorySettings : IPluginSettings
{
    public Dictionary<string, string?> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        Values[key] = valueJson;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEvents : IPluginEvents
{
    private readonly Lock _gate = new();
    private readonly List<Channel<PluginEvent>> _watchers = [];

    public List<PluginEvent> Published { get; } = [];

    public void Publish(string topic, string? payloadJson)
    {
        var item = new PluginEvent(topic, payloadJson);
        List<Channel<PluginEvent>> watchers;
        lock (_gate)
        {
            Published.Add(item);
            watchers = [.. _watchers];
        }

        foreach (var watcher in watchers)
        {
            watcher.Writer.TryWrite(item);
        }
    }

    public List<PluginEvent> Of(string topic)
    {
        lock (_gate)
        {
            return [.. Published.Where(p => p.Topic == topic)];
        }
    }

    public async IAsyncEnumerable<PluginEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<PluginEvent>();
        lock (_gate)
        {
            _watchers.Add(channel);
        }

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel);
            }
        }
    }
}

/// <summary>The page's host: devices, permissions, confirmations, the plugins that are on, and the real server part.</summary>
internal sealed class TestContext(PluginHost server, List<IDeviceInfo> devices) : ICorePluginClientContext
{
    public List<IDeviceInfo> Items { get; } = devices;

    public HashSet<string> Denied { get; } = [];

    public bool Confirm { get; set; } = true;

    public List<string> Opened { get; } = [];

    public bool? TriggersOn { get; set; } = true;

    public ILiveVideoAccess? Video { get; set; }

    public IReadOnlyList<IDeviceInfo> Devices => Items;

    public event EventHandler? DevicesChanged;

    public event EventHandler? PermissionsChanged;

    public ILiveVideoAccess? LiveVideo => Video;

    public bool Has(string permission) => !Denied.Contains(permission);

    public bool? IsPluginOn(string packageId) => packageId == DashboardsPluginInfo.DeviceTriggersId ? TriggersOn : true;

    public Task OpenAsync(string hostPage)
    {
        Opened.Add(hostPage);
        return Task.CompletedTask;
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => server.Plugin.InvokeAsync(method, payloadJson, ct);

    public IAsyncEnumerable<PluginEvent> WatchEventsAsync(CancellationToken ct) => server.Events.WatchAsync(ct);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(Confirm);

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Live videos that record what they were asked to show.</summary>
internal sealed class FakeVideoAccess : ILiveVideoAccess
{
    public List<FakeVideo> Created { get; } = [];

    public ILiveVideo Create(LiveVideoPlace place)
    {
        var video = new FakeVideo(place);
        Created.Add(video);
        return video;
    }
}

internal sealed class FakeVideo(LiveVideoPlace place) : ILiveVideo
{
    public LiveVideoPlace Place { get; } = place;

    public Control View { get; } = new Border();

    public Guid? Shown { get; private set; }

    public int Camera { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler<int>? CameraChosen;

    public void Start(Guid deviceId, int camera = 0)
    {
        Shown = deviceId;
        Camera = camera;
    }

    public void Close() => Shown = null;

    public void Choose(int camera)
    {
        Camera = camera;
        CameraChosen?.Invoke(this, camera);
    }

    public void Dispose() => IsDisposed = true;
}
