using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ImageHealth.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.ImageHealth.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Headless helpers: the real host page around a view, render pumping and PNG capture (OADM_SCREENSHOT_DIR).</summary>
internal static class Headless
{
    public static Window Host(Control view)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(ImageHealthPluginInfo.PluginId, ImageHealthPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1500, Height = 640, Content = new Border { Padding = new Thickness(16), Child = page } };
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

internal sealed record TestDevice(string Address, string? Model, DeviceStatus Status = DeviceStatus.Ok, DeviceCategory Category = DeviceCategory.Camera) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "B8A44F000001";

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.118";

    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    public IReadOnlyList<DeviceApi> Apis => [];
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

/// <summary>Answers the AIHA status request per device: the JSON, an HTTP status, or an exception. Unknown devices: 404.</summary>
internal sealed class FakeVapix : IVapixClientFactory
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, Func<HttpResponseMessage>> Answers { get; } = new();

    public int Requests;

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(new Client(this, deviceId));

    public static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

    public static HttpResponseMessage Status(System.Net.HttpStatusCode code) => new(code) { Content = new StringContent("<html></html>") };

    private sealed class Client(FakeVapix owner, Guid deviceId) : IVapixClient
    {
        public Uri BaseAddress => new("http://device/");

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.Requests);
            Assert.Equal(HttpMethod.Get, request.Method); // read-only
            Assert.Equal(ImageHealthPluginInfo.StatusPath, request.RequestUri!.OriginalString);
            return owner.Answers.TryGetValue(deviceId, out var answer)
                ? Task.FromResult(answer())
                : Task.FromResult(Status(System.Net.HttpStatusCode.NotFound));
        }
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
internal sealed class PluginPageContext(ImageHealthPlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => [];

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(method);
        }

        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(ImageHealthPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }
}

/// <summary>Plugin, hub, fakes wired together with fast timings.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public Rig(ImageHealthOptions? options = null)
    {
        Plugin = new ImageHealthPlugin(options ?? Fast());
    }

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeVapix Vapix { get; } = new();

    public ImageHealthPlugin Plugin { get; }

    public static ImageHealthOptions Fast() => new()
    {
        PublishInterval = TimeSpan.FromMilliseconds(20),
    };

    public async Task<Rig> StartAsync()
    {
        await Plugin.StartAsync(new FakeContext(Devices, Vapix, Hub.For(ImageHealthPluginInfo.PluginId)), CancellationToken.None);
        return this;
    }

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Status answers of the AXIS Q3548-LVE (AXIS OS 12.11.118, AIHA 3.2.2): normal, pending, detected.</summary>
internal static class Recorded
{
    public static string Normal => Read("q3548-status-normal.json");

    public static string Pending => Read("q3548-status-pending.json");

    public static string Detected => Read("q3548-status-detected.json");

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
