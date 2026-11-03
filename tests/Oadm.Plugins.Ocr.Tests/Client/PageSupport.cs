using System.Globalization;
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

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Ocr.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // OADM_TEST_APPEARANCE=Light renders the screenshots in the light appearance; the colors are loaded once per process,
        // so each appearance is its own test run.
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Ocr.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(OcrInfo.PluginId, OcrInfo.DisplayName, view, new OcrPage().HasOwnCards),
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

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

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
        _events.Writer.TryWrite(new PluginEvent(topic, OcrJson.Serialize(payload)));

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

/// <summary>Example readers, readings and a drawn camera picture (no camera, no recognition).</summary>
internal static class Samples
{
    public static readonly TestDevice Gate = new("10.0.0.48", "AXIS Q1798-LE") { Id = Guid.Parse("11111111-0000-0000-0000-000000000048") };

    public static readonly TestDevice Plant = new("10.0.0.202", "AXIS M3215-LVE") { Id = Guid.Parse("11111111-0000-0000-0000-000000000202") };

    public static readonly TestDevice Dock = new("10.0.0.12", "AXIS P3268-LV") { Id = Guid.Parse("11111111-0000-0000-0000-000000000012") };

    public static readonly DateTimeOffset Time = new(2026, 11, 3, 13, 32, 6, TimeSpan.Zero);

    public static Reader ContainerGate => new()
    {
        Id = "r1",
        Name = "Container gate",
        DeviceId = Gate.Id,
        Mode = ReaderMode.Watching,
        Fps = 1,
        Areas =
        [
            new Area { Name = "Container number", Box = new(0.47, 0.27, 0.27, 0.12), Expected = ExpectedText.WithFormat("AAAU ###### #") },
            new Area { Name = "Type", Box = new(0.47, 0.40, 0.12, 0.08) },
            new Area { Name = "Lane", Box = new(0.88, 0.31, 0.10, 0.13), Expected = ExpectedText.WithFormat("A#") },
        ],
    };

    public static Reader WaterMeter => new()
    {
        Id = "r2",
        Name = "Water meter",
        DeviceId = Plant.Id,
        Mode = ReaderMode.OnDemand,
        Areas =
        [
            new Area { Name = "Reading", Box = new(0.32, 0.32, 0.38, 0.18), Expected = ExpectedText.WithFormat("####.#") },
            new Area { Name = "Meter number", Box = new(0.32, 0.53, 0.30, 0.13) },
        ],
    };

    public static Reader DockSign => new()
    {
        Id = "r3",
        Name = "Dock 4 sign",
        DeviceId = Dock.Id,
        Mode = ReaderMode.Watching,
        Fps = 2,
    };

    public static Reader ById(string id) => id switch
    {
        "r1" => ContainerGate,
        "r2" => WaterMeter,
        _ => DockSign,
    };

    /// <summary>The list as the server sends it: the frame rate and the problem per reader, the frame rates offered.</summary>
    public static string ListJson() => OcrJson.Serialize(new
    {
        readers = new object[]
        {
            new { id = "r1", name = "Container gate", deviceId = Gate.Id, mode = "Watching", fps = 1, lastText = "MSKU 123456 7", lastTime = Time },
            new { id = "r3", name = "Dock 4 sign", deviceId = Dock.Id, mode = "Watching", fps = 2, lastText = "DOCK 4", lastTime = Time, problem = "Camera not reachable, trying again in 30 s" },
            new { id = "r2", name = "Water meter", deviceId = Plant.Id, mode = "OnDemand", lastText = "" },
        },
        fpsChoices = new[] { 1, 2, 4, 8, 15 },
        maxWatching = 50,
    });

    public static Reading GateReading(string number = "MSKU 123456 7") => new()
    {
        ReaderId = "r1",
        Time = Time,
        Resolution = new(1600, 900),
        Areas =
        [
            new AreaResult
            {
                Area = "Container number", Text = number, TextRead = number, Confidence = 96, MatchesFormat = true,
                Lines = [new TextLine(number, new(0.485, 0.29, 0.25, 0.08), 96)],
            },
            new AreaResult
            {
                Area = "Type", Text = "22G1", TextRead = "22G1", Confidence = 94, MatchesFormat = true,
                Lines = [new TextLine("22G1", new(0.485, 0.40, 0.09, 0.06), 94)],
            },
            new AreaResult
            {
                Area = "Lane", Text = string.Empty, TextRead = "G3", Confidence = 0, MatchesFormat = false,
                Lines = [new TextLine("G3", new(0.895, 0.33, 0.06, 0.08), 71)],
            },
        ],
    };

    /// <summary>The details of the gate's areas (the lane's lines were too small).</summary>
    public static IReadOnlyList<AreaDetails> GateExtras =>
    [
        new AreaDetails { Area = "Container number", AllMatches = "MSKU 123456 7", MatchBox = new(0.485, 0.29, 0.25, 0.08) },
        new AreaDetails { Area = "Type" },
        new AreaDetails { Area = "Lane", TooSmall = true },
    ];

    /// <summary>
    /// The reading reply JSON as the server sends it: <c>{"reading":{...},"jpeg":...,"details":[...]}</c> (the picture as
    /// PNG bytes in the JPEG field: the page decodes either).
    /// </summary>
    public static string ReplyJson(Reading reading, IReadOnlyList<AreaDetails> details, byte[]? picture) =>
        OcrJson.Serialize(new { reading, jpeg = picture, details });

    public static EngineInfo Engine => new() { RunsOn = "NVIDIA GeForce RTX 5070 Ti", GpuAvailable = true, UseGpu = true, FpsChoices = [1, 2, 4, 8, 15] };

    public static CameraOptions CameraOptions => new()
    {
        VideoSources = [new CameraSource { Number = 1, Name = "View area 1" }, new CameraSource { Number = 2, Name = "View area 2" }],
        Resolutions = [new(2688, 1512), new(1920, 1080), new(1280, 720)],
    };

    /// <summary>The fake server: the list, each reader, its reading, the engine and the camera.</summary>
    public static FakePageContext Context(byte[]? picture = null)
    {
        var ctx = new FakePageContext();
        ctx.DeviceList.AddRange([Gate, Plant, Dock, new TestDevice("10.0.0.90", "AXIS A1601", Category: DeviceCategory.DoorController)]);
        ctx.Answers[OcrMethods.ListReaders] = _ => ListJson();
        ctx.Answers[OcrMethods.GetReader] = json => OcrJson.Serialize(ById(OcrJson.Deserialize<ReaderRequest>(json).ReaderId));
        ctx.Answers[OcrMethods.GetReading] = json => OcrJson.Deserialize<ReaderRequest>(json).ReaderId == "r1"
            ? ReplyJson(GateReading(), GateExtras, picture)
            : "{}";
        ctx.Answers[OcrMethods.GetEngine] = _ => OcrJson.Serialize(Engine);
        ctx.Answers[OcrMethods.GetCameraOptions] = _ => OcrJson.Serialize(CameraOptions);
        ctx.Answers[OcrMethods.SaveReader] = json =>
        {
            var reader = OcrJson.Deserialize<SaveReaderRequest>(json).Reader;
            return OcrJson.Serialize(new SaveReaderReply { Reader = reader with { Id = reader.Id.Length == 0 ? "r9" : reader.Id } });
        };
        ctx.Answers[OcrMethods.DeleteReader] = _ => "{}";
        return ctx;
    }

    /// <summary>Decodes in place (no thread pool in the tests).</summary>
    public static Task<Bitmap?> Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Task.FromResult<Bitmap?>(new Bitmap(stream));
    }

    /// <summary>A drawn container at a gate (1600x900) as PNG bytes; needs the headless session.</summary>
    public static byte[] ContainerPicture()
    {
        using var target = new RenderTargetBitmap(new PixelSize(1600, 900));
        using (var dc = target.CreateDrawingContext())
        {
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#20262c")), new Rect(0, 0, 1600, 900));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#2b2f33")), new Rect(0, 640, 1600, 260));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#3a4550")), new Rect(0, 0, 1600, 120));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#1f5f8a")), new Rect(180, 190, 1180, 470));
            for (var i = 0; i < 30; i++)
            {
                dc.FillRectangle(new SolidColorBrush(Color.Parse("#1b5479")), new Rect(195 + (i * 39), 200, 22, 450));
            }

            dc.FillRectangle(new SolidColorBrush(Color.Parse("#1f5f8a")), new Rect(760, 250, 420, 190));
            var white = new SolidColorBrush(Color.Parse("#f1f1f1"));
            var bold = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Black);
            dc.DrawText(new FormattedText("MSKU 123456 7", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 60, white), new Point(780, 262));
            dc.DrawText(new FormattedText("22G1", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 50, white), new Point(780, 358));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#e8c21c")), new Rect(1420, 300, 140, 90));
            dc.DrawText(new FormattedText("G3", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 46, Brushes.Black), new Point(1445, 315));
            dc.FillRectangle(new SolidColorBrush(Color.Parse("#151515")), new Rect(160, 660, 1220, 40));
        }

        using var stream = new MemoryStream();
        target.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
}
