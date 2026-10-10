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

/// <summary>A device answering getPorts with <see cref="Body"/> (the recorded A9210 by default); read-only.</summary>
internal sealed class FakeVapix : IVapixClientFactory, IVapixClient
{
    public static string Recorded => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "a9210-getports.json"));

    public string Body { get; set; } = Recorded;

    public Func<HttpResponseMessage>? Override { get; set; }

    public int Requests;

    public Uri BaseAddress => new("http://device/");

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

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(this);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        Assert.Equal(IoMonitorPluginInfo.Path, request.RequestUri!.OriginalString);
        Assert.Contains("\"getPorts\"", await request.Content!.ReadAsStringAsync(ct), StringComparison.Ordinal); // read-only
        return Override?.Invoke() ?? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
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

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        Calls.Enqueue(method);
        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
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

    /// <param name="leaseTimeout">Without keep-alives a stream stops after this (default 5 s; the lease test shortens it).</param>
    public Rig(TimeSpan? leaseTimeout = null)
    {
        Plugin = new(new IoMonitorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            RetryDelay = TimeSpan.FromMilliseconds(50),
            LeaseTimeout = leaseTimeout ?? TimeSpan.FromSeconds(5),
            LeaseCheckInterval = TimeSpan.FromMilliseconds(50),
            MaxStreams = 2,
        });
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
