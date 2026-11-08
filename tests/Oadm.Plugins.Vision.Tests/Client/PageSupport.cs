using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Vision.Tests.Client;

// The headless session, the fake page context and the fake live video of the page tests (the same shape as Text
// recognition's page tests).

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // OADM_TEST_APPEARANCE=Light renders the screenshots in the light appearance; the colors are loaded once per process,
        // so each appearance is its own test run.
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Vision.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Headless helpers: one session per test process, the real host page around the view, PNGs (OADM_SCREENSHOT_DIR).</summary>
internal static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> SessionInstance = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    public static HeadlessUnitTestSession Session => SessionInstance.Value;

    public static bool IsLight => Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";

    /// <summary>"dark" or "light": the file name suffix of the screenshots.</summary>
    public static string Appearance => IsLight ? "light" : "dark";

    public static Window Host(Control view, int width = 1500, int height = 1000)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(VisionInfo.PluginId, VisionInfo.DisplayName, view, new VisionPage().HasOwnCards),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static void Pump()
    {
        for (var i = 0; i < 20; i++)
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

internal sealed record TestDevice(string Address, string? Model, DeviceStatus Status = DeviceStatus.Ok, DeviceCategory Category = DeviceCategory.Camera) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "B8A44F000001";

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.118";

    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    public IReadOnlyList<DeviceApi> Apis => [];

    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// The page context with canned answers per method (JSON in, JSON out, or a task to hold a call), the role's permissions,
/// the confirmation's answer and a channel of pushed events: the fake server of the page.
/// </summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();

    public List<IDeviceInfo> DeviceList { get; } = [];

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Func<string?, Task<string?>>> AsyncAnswers { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>False: a user limited to a device group.</summary>
    public bool HasAllDevices { get; set; } = true;

    public IReadOnlyList<IDeviceInfo> Devices
    {
        get
        {
            DeviceReads++;
            return DeviceList;
        }
    }

    /// <summary>How often the page read the device list.</summary>
    public int DeviceReads { get; private set; }

    public event EventHandler? DevicesChanged;

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The host's live video; null: an older OADM without it.</summary>
    public FakeVideoAccess? Video { get; set; } = new();

    public ILiveVideoAccess? LiveVideo => Video;

    public event EventHandler? PermissionsChanged;

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public bool Has(string permission) => !Denied.Contains(permission);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public int CallsOf(string method)
    {
        lock (Calls)
        {
            return Calls.Count(c => c.Method == method);
        }
    }

    public string? LastPayloadOf(string method)
    {
        lock (Calls)
        {
            return Calls.LastOrDefault(c => c.Method == method).Payload;
        }
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Calls)
        {
            Calls.Add((method, payloadJson));
        }

        if (AsyncAnswers.TryGetValue(method, out var later))
        {
            return later(payloadJson);
        }

        return Answers.TryGetValue(method, out var answer)
            ? Task.FromResult(answer(payloadJson))
            : Task.FromException<string?>(new InvalidOperationException("No answer for " + method));
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, VisionJson.Serialize(payload)));

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _events.Reader.WaitToReadAsync(ct))
        {
            while (_events.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }
}

/// <summary>Live videos that record what they were asked to show; <see cref="Still"/> is the picture they show (screenshots).</summary>
internal sealed class FakeVideoAccess : ILiveVideoAccess
{
    public List<FakeVideo> Created { get; } = [];

    /// <summary>A still frame as PNG bytes shown by every video created afterwards; null: an empty surface.</summary>
    public byte[]? Still { get; set; }

    public ILiveVideo Create(LiveVideoPlace place)
    {
        var video = new FakeVideo(place, Still);
        Created.Add(video);
        return video;
    }
}

internal sealed class FakeVideo : ILiveVideo
{
    private readonly byte[]? _still;
    private Control? _view;

    public FakeVideo(LiveVideoPlace place, byte[]? still)
    {
        Place = place;
        _still = still;
    }

    public LiveVideoPlace Place { get; }

    /// <summary>Made on first use (the view model tests run without a UI thread).</summary>
    public Control View => _view ??= MakeView();

    private Control MakeView()
    {
        if (_still is null)
        {
            return new Border();
        }

        using var stream = new MemoryStream(_still);
        return new Image { Source = new Bitmap(stream), Stretch = Stretch.Uniform };
    }

    public Guid? Shown { get; private set; }

    /// <summary>Every Start in order: device and channel.</summary>
    public List<(Guid Device, int Camera)> Starts { get; } = [];

    public int Camera { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler<int>? CameraChosen;

    public void Start(Guid deviceId, int camera = 0)
    {
        Shown = deviceId;
        Camera = camera;
        Starts.Add((deviceId, camera));
    }

    public void Close() => Shown = null;

    public void Choose(int camera)
    {
        Camera = camera;
        CameraChosen?.Invoke(this, camera);
    }

    public void Dispose() => IsDisposed = true;
}

