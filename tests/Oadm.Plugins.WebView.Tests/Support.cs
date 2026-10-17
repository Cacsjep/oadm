using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.WebView.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.WebView.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(WebViewPluginInfo.PluginId, WebViewPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = 1500, Height = 720, Content = new Border { Padding = new Thickness(16), Child = page } };
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

internal sealed record TestDevice(string Address, string? Model, params string[] TagNames) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "ACCC8E" + Random.Shared.Next(0, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;

    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => [];

    public IReadOnlyList<string> Tags => TagNames;
}

/// <summary>The page's host context: devices, and a web access that records what was opened.</summary>
internal sealed class TestContext(List<IDeviceInfo> devices) : ICorePluginClientContext
{
    public List<IDeviceInfo> Items { get; } = devices;

    public FakeWebAccess Web { get; } = new();

    public IReadOnlyList<IDeviceInfo> Devices => Items;

    public event EventHandler? DevicesChanged;

    public IDeviceWebAccess? DeviceWeb => Web;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeWebAccess : IDeviceWebAccess
{
    public List<FakeSession> Sessions { get; } = [];

    public Dictionary<Guid, string> Refused { get; } = [];

    public Task<IDeviceWebSession> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        if (Refused.TryGetValue(deviceId, out var reason))
        {
            throw new DeviceWebRefusedException(reason);
        }

        var session = new FakeSession(deviceId, Sessions.Count + 1);
        Sessions.Add(session);
        return Task.FromResult<IDeviceWebSession>(session);
    }
}

internal sealed class FakeSession(Guid deviceId, int number) : IDeviceWebSession
{
    public Guid DeviceId { get; } = deviceId;

    public Uri Origin { get; } = new($"http://127.0.0.1:{50000 + number}/");

    public bool IsDisposed { get; private set; }

    public Uri StartAddress(string path) => new(Origin, "/__oadm/start?path=" + Uri.EscapeDataString(path));

    public void Dispose() => IsDisposed = true;
}
