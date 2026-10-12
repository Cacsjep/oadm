using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LiveView.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.LiveView.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(LiveViewPluginInfo.PluginId, LiveViewPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = 1500, Height = 760, Content = new Border { Padding = new Thickness(16), Child = page } };
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

    public static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (Dispatcher.UIThread.CheckAccess() && Application.Current is not null)
            {
                Pump(); // headless tests; the view model tests run without a UI thread
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
}

/// <summary>The server part with its settings and events in memory.</summary>
internal sealed class PluginHost : IDisposable
{
    public PluginHost()
    {
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(Settings);
        ctx.Events.Returns(Events);
        ctx.Logger.Returns(NullLogger.Instance);
        Plugin.StartAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
    }

    public LiveViewPlugin Plugin { get; } = new();

    public MemorySettings Settings { get; } = new();

    public RecordingEvents Events { get; } = new();

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
    private readonly List<Channel<PluginEvent>> _watchers = [];

    public List<PluginEvent> Published { get; } = [];

    public void Publish(string topic, string? payloadJson)
    {
        var item = new PluginEvent(topic, payloadJson);
        Published.Add(item);
        foreach (var watcher in _watchers.ToList())
        {
            watcher.Writer.TryWrite(item);
        }
    }

    public async IAsyncEnumerable<PluginEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<PluginEvent>();
        _watchers.Add(channel);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
            {
                yield return item;
            }
        }
        finally
        {
            _watchers.Remove(channel);
        }
    }
}

/// <summary>The page's host: devices, permissions, confirmations, and the real server part behind InvokeAsync.</summary>
internal sealed class TestContext(PluginHost server, List<IDeviceInfo> devices) : ICorePluginClientContext
{
    public List<IDeviceInfo> Items { get; } = devices;

    public HashSet<string> Denied { get; } = [];

    public bool Confirm { get; set; } = true;

    public List<string> Confirmations { get; } = [];

    public ILiveVideoAccess? Video { get; set; }

    public IReadOnlyList<IDeviceInfo> Devices => Items;

    public event EventHandler? DevicesChanged;

    public event EventHandler? PermissionsChanged;

    public ILiveVideoAccess? LiveVideo => Video;

    public bool Has(string permission) => !Denied.Contains(permission);

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => server.Plugin.InvokeAsync(method, payloadJson, ct);

    public IAsyncEnumerable<PluginEvent> WatchEventsAsync(CancellationToken ct) => server.Events.WatchAsync(ct);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add(message);
        return Task.FromResult(Confirm);
    }

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
