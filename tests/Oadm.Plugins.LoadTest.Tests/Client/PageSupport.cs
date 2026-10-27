using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.LoadTest.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LoadTest.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // OADM_TEST_APPEARANCE=Light renders the screenshots in the light appearance (colors and theme variant); the
        // colors are loaded once per process, so each appearance is its own test run.
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.LoadTest.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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

    public static Window Host(Control view, int width = 1500, int height = 1000)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(LoadTestInfo.PluginId, LoadTestInfo.DisplayName, view, new LoadTestPage().HasOwnCards),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static bool IsLight => Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";

    /// <summary>"dark" or "light": the file name suffix of the screenshots.</summary>
    public static string Appearance => IsLight ? "light" : "dark";

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
/// The page context with canned answers per method (JSON in, JSON out), the role's permissions, the confirmation's answer
/// and a channel of pushed events.
/// </summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();

    public List<IDeviceInfo> DeviceList { get; } = [];

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public Dictionary<string, string> LocalSettings { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public event EventHandler? PermissionsChanged;

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public bool Has(string permission) => !Denied.Contains(permission);

    public string? GetLocalSetting(string key) => LocalSettings.GetValueOrDefault(key);

    public void SetLocalSetting(string key, string? value)
    {
        if (value is null)
        {
            LocalSettings.Remove(key);
        }
        else
        {
            LocalSettings[key] = value;
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
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

        return Answers.TryGetValue(method, out var answer)
            ? Task.FromResult(answer(payloadJson))
            : Task.FromException<string?>(new InvalidOperationException("No answer for " + method));
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, LoadTestJson.Serialize(payload)));

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
