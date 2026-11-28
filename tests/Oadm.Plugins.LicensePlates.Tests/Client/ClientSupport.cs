using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.LicensePlates.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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

    public static string Appearance => IsLight ? "light" : "dark";

    /// <summary>The view in the client's host page (title, subtitle, the header's live status) like the rail shows it.</summary>
    public static Window Host(Control view, int width = 1600, int height = 900)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(LicensePlatesInfo.PluginId, LicensePlatesInfo.DisplayName, view, true),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    /// <summary>A dialog window sized for its screenshot.</summary>
    public static Window Dialog(Window window)
    {
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
                frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
            }
        }
    }

    /// <summary>A plate crop like ALPV sends: white plate, the red-white-red strip of Austria, the text in black.</summary>
    public static IImage PlateCrop(string text)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(240, 56));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(70, 70, 72)), new Rect(0, 0, 240, 56));
            ctx.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 2), new Rect(6, 6, 228, 44), 4, 4);
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(200, 16, 46)), new Rect(8, 8, 224, 6));
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(200, 16, 46)), new Rect(8, 42, 224, 6));
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 22, Brushes.Black);
            ctx.DrawText(formatted, new Point((240 - formatted.Width) / 2, (56 - formatted.Height) / 2));
        }

        return bitmap;
    }
}

/// <summary>A device of the client's device list.</summary>
internal sealed record FakeDevice(Guid Id, string Address, string? Model, string? HostName, IReadOnlyList<string> Tags) : IDeviceInfo
{
    public string Serial => "ACCC8E" + Id.ToString("N")[..6].ToUpperInvariant();

    public string? FirmwareVersion => "12.11.147";

    public DeviceStatus Status => DeviceStatus.Ok;

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => [];
}

/// <summary>The host's live video in tests: a dark road picture in place of the stream; Start records the device.</summary>
internal sealed class FakeLiveVideoAccess : ILiveVideoAccess
{
    public List<(Guid Device, int Camera)> Started { get; } = [];

    public int Disposed { get; private set; }

    public ILiveVideo Create(LiveVideoPlace place) => new Video(this);

    private sealed class Video(FakeLiveVideoAccess owner) : ILiveVideo
    {
        private Control? _view;

        // Built on first use, which is inside the headless session (the view-model tests never touch it): Avalonia objects
        // belong to the thread that made them.
        public Control View => _view ??= Picture();

        // A 16:9 picture letterboxed like the live video, so the areas over it sit where the picture is.
        private static Viewbox Picture() => new()
        {
            Stretch = Stretch.Uniform,
            Child = new Canvas
            {
                Width = 1600,
                Height = 900,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromRgb(96, 108, 120), 0), new GradientStop(Color.FromRgb(58, 60, 64), 0.45), new GradientStop(Color.FromRgb(38, 39, 42), 1) },
                },
                Children =
                {
                    // The lane markings of a garage entry, seen from above the barrier.
                    new Border { Width = 18, Height = 520, Background = new SolidColorBrush(Color.FromRgb(220, 220, 210)), [Canvas.LeftProperty] = 790.0, [Canvas.TopProperty] = 380.0 },
                    new Border { Width = 1600, Height = 14, Background = new SolidColorBrush(Color.FromRgb(230, 190, 40)), [Canvas.TopProperty] = 360.0 },
                },
            },
        };

        public int Camera { get; private set; }

        public event EventHandler<int>? CameraChosen
        {
            add { }
            remove { }
        }

        public void Start(Guid deviceId, int camera = 0)
        {
            Camera = camera;
            owner.Started.Add((deviceId, camera));
        }

        public void Close()
        {
        }

        public void Dispose() => owner.Disposed++;
    }
}

/// <summary>The server's folders in tests: the check refuses one folder.</summary>
internal sealed class FakeServerFolders : IServerFolders
{
    public const string Refused = @"C:\Windows\Plates";

    public Task<ServerFolderListing> ListAsync(string? path, CancellationToken ct) =>
        Task.FromResult(new ServerFolderListing(path ?? string.Empty, null, [new ServerFolder("Plates", @"D:\Plates")], false, null));

    public Task<ServerFolder> CreateAsync(string parentPath, string name, CancellationToken ct) =>
        Task.FromResult(new ServerFolder(name, Path.Combine(parentPath, name)));

    public Task<string?> CheckAsync(string path, CancellationToken ct) =>
        Task.FromResult<string?>(string.Equals(path, Refused, StringComparison.OrdinalIgnoreCase) ? ServerFolderTexts.SystemFolder : null);
}

/// <summary>The page context with canned answers per method, the confirmation's answer and a channel of pushed events.</summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Func<string?, byte[]>> Downloads { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public List<(Guid Device, string Path)> OpenedPages { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public bool ConfirmAnswer { get; set; } = true;

    public bool Admin { get; set; } = true;

    public bool IsAdministrator => Admin;

    public FakeLiveVideoAccess Video { get; } = new();

    public ILiveVideoAccess? LiveVideo => Video;

    public IServerFolders? ServerFolders { get; } = new FakeServerFolders();

    /// <summary>Holds the calls of one method until its task completes.</summary>
    public Dictionary<string, Task> MethodGates { get; } = new(StringComparer.Ordinal);

    public bool Has(string permission) => !Denied.Contains(permission);

    public event EventHandler? PermissionsChanged;

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }

    public bool OpenDevicePage(Guid deviceId, string path)
    {
        OpenedPages.Add((deviceId, path));
        return true;
    }

    public string? GetLocalSetting(string key) => _settings.GetValueOrDefault(key);

    public void SetLocalSetting(string key, string? value)
    {
        if (value is null)
        {
            _settings.Remove(key);
        }
        else
        {
            _settings[key] = value;
        }
    }

    public int CallsOf(string method) => Calls.Count(c => c.Method == method);

    public T LastPayload<T>(string method) => Samples.Parse<T>(Calls.Last(c => c.Method == method).Payload);

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((method, payloadJson));
        if (MethodGates.TryGetValue(method, out var held))
        {
            await held;
        }

        return Answers.TryGetValue(method, out var answer) ? answer(payloadJson) : throw new InvalidOperationException("No answer for " + method);
    }

    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct)
    {
        Calls.Add((method, payloadJson));
        var bytes = Downloads.TryGetValue(method, out var make) ? make(payloadJson) : Encoding.UTF8.GetBytes("file");
        return Task.FromResult(new PluginDownload(new MemoryStream(bytes), "file"));
    }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        Calls.Add(("upload", localPath));
        return Task.FromResult(new UploadedFile("upload-1", Path.GetFileName(localPath), 100, "abc"));
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, JsonSerializer.Serialize(payload, payload.GetType(), LicensePlatesJson.Options)));

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

/// <summary>The page's dialogs in tests: each one records its view model and answers as set.</summary>
internal sealed class FakeDialogs : IPageDialogs
{
    public List<object> Shown { get; } = [];

    /// <summary>What a dialog does before it answers (fill the form, press Save); null: answer <see cref="Answer"/>.</summary>
    public Func<object, Task<bool>>? Handle { get; set; }

    public bool Answer { get; set; } = true;

    public IReadOnlyList<Guid>? PickedCameras { get; set; }

    public string? CsvPath { get; set; }

    public byte[] SavedBytes { get; private set; } = [];

    public List<string> SaveNames { get; } = [];

    public Task<bool> EditPlateAsync(PlateEditorViewModel editor) => Show(editor);

    public Task<bool> ImportAsync(ImportViewModel import) => Show(import);

    public Task<bool> TagsAsync(TagsDialogViewModel tags) => Show(tags);

    public Task<bool> SetupAsync(SetupViewModel setup) => Show(setup);

    public Task<bool> CopySettingsAsync(CopySettingsViewModel copy) => Show(copy);

    public Task<bool> SettingsAsync(SettingsViewModel settings) => Show(settings);

    public Task<IReadOnlyList<Guid>?> PickCamerasAsync(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<Guid> selected, string heading)
    {
        Shown.Add(devices);
        return Task.FromResult(PickedCameras);
    }

    public Task<bool> PickCameraChoiceAsync(PageDeviceChoice choice) => Task.FromResult(false);

    public Task<string?> OpenCsvAsync() => Task.FromResult(CsvPath);

    public Task<Stream?> SaveFileAsync(string title, string suggestedName)
    {
        SaveNames.Add(suggestedName);
        return Task.FromResult<Stream?>(new KeepOpenStream(this));
    }

    private async Task<bool> Show(object dialog)
    {
        Shown.Add(dialog);
        return Handle is { } handle ? await handle(dialog) : Answer;
    }

    /// <summary>A file that keeps what was written when the page disposes it.</summary>
    private sealed class KeepOpenStream(FakeDialogs owner) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            owner.SavedBytes = ToArray();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The sample server of the client tests: Austrian-style plates, garages as plate tags, cameras with every status (one whose
/// image settings differ), a log with every result, low confidences, one deviated read and a gap, and stats.
/// </summary>
internal static class Samples
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test/Vienna", TimeSpan.FromHours(1), "Vienna", "Vienna");

    /// <summary>The client's "now" (UTC+1).</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 14, 5, 0, TimeSpan.FromHours(1));

    public static readonly Guid GarageNord = Guid.Parse("1a1a1a1a-0001-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid GarageSued = Guid.Parse("1a1a1a1a-0002-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid Staff = Guid.Parse("1a1a1a1a-0003-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid Visitors = Guid.Parse("1a1a1a1a-0004-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid Delivery = Guid.Parse("1a1a1a1a-0005-4c2d-9e8f-0a1b2c3d4e5f");

    public static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(Now);
        clock.SetLocalTimeZone(Zone);
        return clock;
    }

    public static IReadOnlyList<TagInfo> Tags =>
    [
        new(GarageNord, "Garage Nord", PlateTagColor.Blue, 412, 3),
        new(GarageSued, "Garage Süd", PlateTagColor.Green, 268, 2),
        new(Staff, "Staff", PlateTagColor.Teal, 96, 5),
        new(Visitors, "Visitors", PlateTagColor.Amber, 31, 1),
        new(Delivery, "Delivery", PlateTagColor.Orange, 12, 4),
    ];

    public static Guid CameraId(int i) => Guid.Parse($"7e7e7e7e-{i:D4}-4c2d-9e8f-0a1b2c3d4e5f");

    /// <summary>12 cameras of the client's device list; the first 11 are in the plugin.</summary>
    public static IEnumerable<IDeviceInfo> Devices() =>
        Enumerable.Range(1, 12).Select(i => (IDeviceInfo)new FakeDevice(CameraId(i), $"10.20.0.{40 + i}", "AXIS P3265-LVE", Name(i), []));

    private static string Name(int i) => i switch
    {
        1 => "nord-entry",
        2 => "nord-exit",
        3 => "sued-entry",
        4 => "sued-exit",
        5 => "staff-gate",
        6 => "delivery-ramp",
        7 => "visitor-lane",
        8 => "loading-bay",
        9 => "east-barrier",
        10 => "west-barrier",
        11 => "site-b-gate",
        _ => "lobby",
    };

    public static IReadOnlyList<ImageValue> ImageValues(bool differ) =>
    [
        new("WDR", "On", "On"),
        new("Contrast", differ ? "50" : "60", "60"),
        new("Local contrast", differ ? "30" : "10", "10"),
        new("Tone mapping", differ ? "40" : "0", "0"),
        new("Max gain", differ ? "60" : "51", "51"),
        new("Shutter speed", differ ? "1/60" : "1/100", "1/100"),
    ];

    /// <summary>A camera with every status; the second one's image settings differ from ALPV's recommendation.</summary>
    public static IReadOnlyList<CameraRow> Cameras()
    {
        CameraRow Row(int i, CameraStatus status, string? site = null, string? text = null, string? version = "3.1.7", IReadOnlyList<Guid>? tags = null, bool? image = true) => new()
        {
            DeviceId = CameraId(i),
            Address = $"10.20.0.{40 + i}",
            HostName = Name(i),
            Name = i switch { 1 => "Garage Nord entry", 2 => "Garage Nord exit", _ => string.Empty },
            Model = "AXIS P3265-LVE",
            Site = site,
            AlpvVersion = version,
            Status = status,
            StatusText = text ?? LicensePlateTexts.Status(status, site),
            Since = status == CameraStatus.ConnectionLost ? Now.AddMinutes(-7) : null,
            AllowedTags = tags ?? [GarageNord, Staff],
            ImageSettings = image is { } ok ? new ImageSettingsInfo(ok, ImageValues(!ok)) : null,
        };

        return
        [
            Row(1, CameraStatus.Connected, tags: [GarageNord, Staff, Visitors]),
            Row(2, CameraStatus.Connected, tags: [GarageNord, Staff], image: false),
            Row(3, CameraStatus.ConnectionLost, tags: [GarageSued, Staff]),
            Row(4, CameraStatus.EventsOff, tags: [GarageSued]),
            Row(5, CameraStatus.AlpvStopped, tags: [Staff]),
            Row(6, CameraStatus.AlpvNotInstalled, version: null, tags: [Delivery], image: null),
            Row(7, CameraStatus.AlpvNoLicense, tags: [Visitors]),
            Row(8, CameraStatus.AlpvNeedsVersion3, version: "2.8.4", tags: [Delivery], image: null),
            Row(9, CameraStatus.OtherSensor, tags: [Delivery]),
            Row(10, CameraStatus.Connecting, tags: [], image: null),
            Row(11, CameraStatus.SiteOffline, site: "Site B", tags: [GarageNord]),
        ];
    }

    public static CamerasReply CamerasReply() => new(Cameras(), 2, 11);

    /// <summary>Plates of the list: Austrian-style, one ended and still there, one with Delete when it ends.</summary>
    public static IReadOnlyList<Plate> Plates() =>
    [
        new() { Text = "W12345A", Name = "Anna Huber", Tags = [GarageNord, Staff] },
        new() { Text = "G4711XY", Name = "Bäckerei Gruber", Tags = [Delivery], AllowedFrom = new DateTime(2026, 11, 1, 6, 0, 0), AllowedTo = new DateTime(2027, 1, 31, 18, 0, 0), DeleteWhenEnds = true },
        new() { Text = "L8820BB", Name = "Lukas Berger", Tags = [GarageSued] },
        new() { Text = "S1234K", Name = "Sabine Koller", Tags = [GarageNord, GarageSued, Staff] },
        new() { Text = "KU5521C", Name = "Visitor Müller", Tags = [Visitors], AllowedFrom = new DateTime(2026, 11, 20, 8, 0, 0), AllowedTo = new DateTime(2026, 11, 26, 18, 0, 0) },
        new() { Text = "BN417HA", Name = "Max Steiner", Tags = [GarageNord] },
        new() { Text = "W99887Z", Name = "Fleet car 12", Tags = [Staff] },
        new() { Text = "GU3310M", Name = "Paketdienst", Tags = [Delivery] },
        new() { Text = "WU4040K", Name = "Clara Wagner", Tags = [GarageSued, Visitors] },
        new() { Text = "KO1212T", Name = "Thomas Leitner", Tags = [] },
        new() { Text = "AXIS12**", Name = "Company cars", Tags = [Staff, GarageNord] },
        new() { Text = "GU7**AB", Name = "Delivery vans", Tags = [Delivery] },
    ];

    public static PlateRead Read(long id, int minutesAgo, int camera, string plate, ReadResult result, int confidence, string? name = null, string[]? tags = null,
        ReadReason? reason = null, string? readAs = null, PlateDirection direction = PlateDirection.In, DateTime? reasonTime = null) => new()
    {
        Id = id,
        Time = Now.AddMinutes(-minutesAgo).AddSeconds(-id % 47),
        CameraId = CameraId(camera),
        Camera = Name(camera),
        Plate = plate,
        ReadAs = readAs,
        Result = result,
        Reason = reason ?? (result == ReadResult.Allowed ? ReadReason.Exact : result == ReadResult.Unknown ? ReadReason.NoPlate : ReadReason.NoTagHere),
        ReasonTime = reasonTime,
        Name = name ?? string.Empty,
        Tags = tags ?? [],
        Direction = direction,
        Area = 1,
        Country = "AUT",
        Confidence = confidence,
        VehicleType = id % 5 == 0 ? "Truck" : "Car",
        VehicleColor = (id % 4) switch { 0 => "Grey", 1 => "White", 2 => "Black", _ => "Blue" },
        VehicleMake = (id % 3) switch { 0 => "Volkswagen", 1 => "Skoda", _ => "BMW" },
        VehicleModel = (id % 3) switch { 0 => "Golf", 1 => "Octavia", _ => "X3" },
        View = id % 2 == 0 ? "rear" : "front",
        HasPicture = true,
        ListEntry = result == ReadResult.Unknown ? null : plate,
    };

    /// <summary>The newest reads first: every result, two low confidences, one deviated read, a gap.</summary>
    public static IReadOnlyList<PlateRead> Reads() =>
    [
        Read(140, 0, 1, "W12345A", ReadResult.Allowed, 97, "Anna Huber", ["Garage Nord", "Staff"]),
        Read(139, 1, 2, "G4711XY", ReadResult.Allowed, 94, "Bäckerei Gruber", ["Delivery"], direction: PlateDirection.Out),
        Read(138, 2, 1, "BN417HA", ReadResult.Allowed, 81, "Max Steiner", ["Garage Nord"], ReadReason.Near, readAs: "BN4I7HA"),
        Read(137, 4, 3, "WB815XA", ReadResult.Unknown, 72),
        Read(136, 5, 1, "KU5521C", ReadResult.NotAllowed, 92, "Visitor Müller", ["Visitors"], ReadReason.Ended, reasonTime: new DateTime(2026, 11, 26, 18, 0, 0)),
        Read(135, 7, 3, "AXIS1234", ReadResult.Allowed, 99, "Company cars", ["Staff", "Garage Nord"]) with { ListEntry = "AXIS12**" }, // a wildcard plate
        Read(134, 9, 2, "S1234K", ReadResult.Allowed, 88, "Sabine Koller", ["Garage Nord", "Garage Süd", "Staff"], direction: PlateDirection.Out),
        Read(133, 12, 4, "GU3310M", ReadResult.NotAllowed, 90, "Paketdienst", ["Delivery"]),
        Read(132, 14, 1, "ZZ71", ReadResult.Unknown, 54, direction: PlateDirection.Unknown),
        Read(131, 17, 2, "W99887Z", ReadResult.Allowed, 96, "Fleet car 12", ["Staff"], direction: PlateDirection.Out),
        new PlateRead { Id = 130, Time = Now.AddMinutes(-26), CameraId = CameraId(3), Camera = Name(3), Gap = true, Until = Now.AddMinutes(-18) },
        Read(129, 31, 3, "WU4040K", ReadResult.Allowed, 93, "Clara Wagner", ["Garage Süd", "Visitors"]),
        Read(128, 33, 1, "KO1212T", ReadResult.NotAllowed, 95, "Thomas Leitner"),
        Read(127, 38, 4, "SL3021R", ReadResult.Unknown, 66),
        Read(126, 41, 2, "W12345A", ReadResult.Allowed, 98, "Anna Huber", ["Garage Nord", "Staff"], direction: PlateDirection.Out),
        Read(125, 47, 1, "LL5522D", ReadResult.Unknown, 87),
    ];

    private static readonly string[] SiteCameras =
    [
        "Gate 1 entry", "Gate 1 exit", "Gate 2 entry", "Gate 2 exit", "P1 ramp up", "P1 ramp down", "P2 ramp up", "P2 ramp down",
        "Loading dock A", "Loading dock B", "Visitor lane", "Staff lane", "North barrier", "South barrier", "Truck scale in",
        "Truck scale out", "Bus lane", "Fire lane", "Hotel drop-off", "Warehouse 3",
    ];

    /// <summary>The reads of an hour of the day: night, the morning and evening peaks.</summary>
    private static int Profile(int hour) => hour switch { < 6 => 2 + hour, 7 or 8 => 190 + (hour * 7), 16 or 17 => 150, > 20 => 30, _ => 40 + (hour * 5) };

    /// <summary>
    /// The Stats of the samples: 4 cameras (nord-entry ... sued-exit, the last below 60, the third below 75 by the hour) or
    /// <paramref name="cameras"/> cameras of a large site; every fourth camera sends no vehicle confidence. Today by the hour
    /// (15 hours to 14:05) or the last 7 days by the day.
    /// </summary>
    public static StatsReply Stats(int cameras = 4, StatsStep step = StatsStep.Hour)
    {
        double[] fourConfidence = [91.4, 89.9, 78.2, 58.5];
        string[] fourNames = ["nord-entry", "nord-exit", "sued-entry", "sued-exit"];
        var steps = step == StatsStep.Hour ? 15 : 7;
        var start = step == StatsStep.Hour ? new DateTimeOffset(Now.Date, Now.Offset) : new DateTimeOffset(Now.Date.AddDays(-6), Now.Offset);
        var series = new List<CameraSeries>();
        for (var i = 0; i < cameras; i++)
        {
            var name = cameras == 4 ? fourNames[i] : SiteCameras[i % SiteCameras.Length] + (i >= SiteCameras.Length ? $" {(i / SiteCameras.Length) + 1}" : string.Empty);
            var weight = 1.0 / (1 + (i * 0.35));
            var average = cameras == 4 ? fourConfidence[i] : 95 - ((i * 7) % 41);
            var reads = Enumerable.Range(0, steps).Select(s => (int)Math.Round((step == StatsStep.Hour ? Profile(s) : 1_800 + ((s * 37) % 400)) * weight / 1.6)).ToArray();
            var perStep = reads.Select((r, s) => r == 0 ? (double?)null : Math.Round(Math.Clamp(average + (((s * 13) + i) % 9) - 4, 0, 100), 1)).ToArray();
            series.Add(new CameraSeries(CameraId(i + 1), name, reads.Sum(), average, i % 4 == 3 ? null : Math.Round(average - 3.5, 1), reads, perStep));
        }

        var totalReads = Enumerable.Range(0, steps).Select(s => series.Sum(c => c.ReadsPerStep[s])).ToArray();
        var totalConfidence = Enumerable.Range(0, steps).Select(s =>
        {
            var n = series.Sum(c => c.ReadsPerStep[s]);
            return n == 0 ? (double?)null : Math.Round(series.Sum(c => c.ReadsPerStep[s] * (c.ConfidencePerStep[s] ?? 0)) / n, 1);
        }).ToArray();
        return new StatsReply
        {
            Totals = cameras == 4 ? new ReadTotals(96, 1_812, 11_946, 49_631) : new ReadTotals(cameras * 41, cameras * 790, cameras * 5_480, cameras * 81_200),
            Results = cameras == 4 ? new ResultCounts(1_204, 86, 211) : new ResultCounts((int)(totalReads.Sum() * 0.8), (int)(totalReads.Sum() * 0.05), totalReads.Sum() - (int)(totalReads.Sum() * 0.8) - (int)(totalReads.Sum() * 0.05)),
            AverageConfidence = 86.4,
            Start = start,
            Step = step,
            Steps = steps,
            TotalReads = totalReads,
            TotalConfidence = totalConfidence,
            Cameras = [.. series.OrderByDescending(c => c.Reads)],
        };
    }

    public static SetupRead Setup(bool rotated = false) => new()
    {
        DeviceId = CameraId(1),
        AlpvVersion = "3.1.7",
        Areas =
        [
            new AlpvArea { Id = 1, Active = true, Angle = 180_000, Corners = [new(120, 420), new(560, 380), new(610, 860), new(80, 900)] },
            new AlpvArea { Id = 2, Active = true, Angle = 0, Corners = [new(640, 380), new(930, 400), new(960, 880), new(700, 860), new(660, 620)] },
        ],
        Recognition = new RecognitionSettings { Region = "5", CameraLocation = "AUT", State = string.Empty, PlateWidthMin = 50, PlateWidthMax = 300, CharactersMin = 4, CharactersMax = 10, ConfidenceThreshold = 0.25 },
        Events = new EventSwitches(true, true, false),
        Rotated = rotated,
        LastRead = Now.AddMinutes(-1),
        AllowedTags = [GarageNord, Staff, Visitors],
    };

    /// <summary>A context that answers like the server.</summary>
    public static FakePageContext Context(IReadOnlyList<PlateRead>? reads = null, IReadOnlyList<Plate>? plates = null)
    {
        var ctx = new FakePageContext();
        ctx.DeviceList.AddRange(Devices());
        var allReads = reads ?? Reads();
        var store = (plates ?? Plates()).ToList();
        ctx.Answers[LicensePlatesMethods.Tags] = _ => Serialize(new TagsReply(Tags));
        ctx.Answers[LicensePlatesMethods.AlpvLists] = _ => Serialize(AlpvListData.Lists);
        ctx.Answers[LicensePlatesMethods.VehicleMakes] = _ => Serialize(new VehicleNamesReply(["(None)", "Audi", "BMW", "Skoda", "Volkswagen"]));
        ctx.Answers[LicensePlatesMethods.VehicleModels] = payload => Serialize(new VehicleNamesReply(Parse<VehicleNamesRequest>(payload).Make == "Volkswagen" ? ["Caddy", "Golf", "Passat"] : []));
        ctx.Answers[LicensePlatesMethods.Cameras] = _ => Serialize(CamerasReply());
        ctx.Answers[LicensePlatesMethods.Plates] = payload =>
        {
            var request = PluginJson.ParseOrEmpty<PlatesRequest>(payload, LicensePlatesJson.Options);
            var start = request.After is null ? 0 : store.FindIndex(p => p.Text == request.After) + 1;
            var page = store.Skip(start).Take(request.Limit).ToList();
            var next = start + page.Count < store.Count ? page[^1].Text : null;
            return Serialize(new PlatesPage(page, next, store.Count));
        };
        ctx.Answers[LicensePlatesMethods.Log] = payload =>
        {
            var request = PluginJson.ParseOrEmpty<LogPageRequest>(payload, LicensePlatesJson.Options);
            IEnumerable<PlateRead> rows = allReads;
            if (request.After is { } after)
            {
                rows = rows.Where(r => r.Id > after.Id);
            }

            if (request.Before is { } before)
            {
                rows = rows.Where(r => r.Id < before.Id);
            }

            if (request.Query.Results is { Count: > 0 } results)
            {
                rows = rows.Where(r => !r.Gap && results.Contains(r.Result));
            }

            var list = rows.Take(request.Limit).ToList();
            var more = rows.Skip(request.Limit).Any();
            return Serialize(new LogPage(list, more && list.Count > 0 ? new LogCursor(list[^1].Time.ToUnixTimeMilliseconds(), list[^1].Id) : null));
        };
        ctx.Answers[LicensePlatesMethods.LogCount] = _ => Serialize(new CountReply(48_213));
        ctx.Answers[LicensePlatesMethods.LogStatus] = _ => Serialize(new LogStatus { ReadsToday = 1_204, UsedBytes = 3_100_000_000, MaxBytes = 20_000_000_000, KeepDays = 30, HasFolder = true });
        ctx.Answers[LicensePlatesMethods.Picture] = payload =>
        {
            var id = Parse<ReadIdRequest>(payload).Id;
            var read = allReads.FirstOrDefault(r => r.Id == id);
            return Serialize(new PictureReply(read is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(read.ReadAs ?? read.Plate))));
        };
        ctx.Answers[LicensePlatesMethods.Stats] = _ => Serialize(Stats());
        ctx.Answers[LicensePlatesMethods.SavePlate] = payload =>
        {
            var request = Parse<SavePlateRequest>(payload);
            if (request.Original is null && store.Any(p => p.Text == request.Plate.Text))
            {
                return Serialize(new SavePlateReply(null, new Dictionary<string, string> { ["text"] = LicensePlateTexts.PlateExists }));
            }

            store.RemoveAll(p => p.Text == (request.Original ?? request.Plate.Text));
            store.Add(request.Plate);
            return Serialize(new SavePlateReply(request.Plate));
        };
        ctx.Answers[LicensePlatesMethods.DeletePlates] = payload =>
        {
            var texts = Parse<PlateTextsRequest>(payload).Plates;
            return Serialize(new CountReply(store.RemoveAll(p => texts.Contains(p.Text))));
        };
        ctx.Answers[LicensePlatesMethods.ChangePlateTags] = payload => Serialize(new CountReply(Parse<ChangePlateTagsRequest>(payload).Plates.Count));
        ctx.Answers[LicensePlatesMethods.SaveTag] = payload =>
        {
            var tag = Parse<SaveTagRequest>(payload).Tag;
            return tag.Name == "Staff" && tag.Id != Staff
                ? Serialize(new SaveTagReply(null, new Dictionary<string, string> { ["name"] = "A plate tag with this name exists." }))
                : Serialize(new SaveTagReply(tag with { Id = tag.Id == Guid.Empty ? Guid.NewGuid() : tag.Id }));
        };
        ctx.Answers[LicensePlatesMethods.DeleteTag] = payload =>
        {
            var request = Parse<DeleteTagRequest>(payload);
            return Serialize(new DeleteTagReply(request.Confirmed, 412, 3));
        };
        ctx.Answers[LicensePlatesMethods.ImportPreview] = _ => Serialize(new ImportPreview("preview-1", ImportFormat.Oadm, 3, 1, 4, 2));
        ctx.Answers[LicensePlatesMethods.ImportRows] = _ => Serialize(new ImportRowsPage(ImportRows(), null));
        ctx.Answers[LicensePlatesMethods.ImportSave] = _ => Serialize(new ImportResult(3, 1));
        ctx.Answers[LicensePlatesMethods.AddCameras] = _ => Serialize(CamerasReply());
        ctx.Answers[LicensePlatesMethods.RemoveCameras] = payload =>
        {
            var ids = Parse<DevicesRequest>(payload).DeviceIds;
            var rows = Cameras().Where(c => !ids.Contains(c.DeviceId)).ToList();
            return Serialize(new CamerasReply(rows, rows.Count(r => r.Status == CameraStatus.Connected), rows.Count));
        };
        ctx.Answers[LicensePlatesMethods.RefreshCameras] = _ => Serialize(CamerasReply());
        ctx.Answers[LicensePlatesMethods.SetAllowedTags] = _ => Serialize(CamerasReply());
        ctx.Answers[LicensePlatesMethods.ReadSetup] = _ => Serialize(Setup());
        ctx.Answers[LicensePlatesMethods.SaveSetup] = _ => Serialize(new SetupSavedReply(["Saved the areas."]));
        ctx.Answers[LicensePlatesMethods.CopySettings] = payload => Serialize(new TasksReply([.. Parse<CopySettingsRequest>(payload).TargetIds.Select(_ => Guid.NewGuid())]));
        ctx.Answers[LicensePlatesMethods.UseRecommendedImageSettings] = payload => Serialize(new TasksReply([.. Parse<DevicesRequest>(payload).DeviceIds.Select(_ => Guid.NewGuid())]));
        ctx.Answers[LicensePlatesMethods.Settings] = _ => Serialize(new LicensePlateSettings { Folder = @"D:\Plates" });
        ctx.Answers[LicensePlatesMethods.SaveSettings] = payload =>
        {
            var settings = Parse<LicensePlateSettings>(payload);
            return settings.Folder is { } folder && folder.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase)
                ? Serialize(new SaveSettingsReply(null, new Dictionary<string, string> { ["folder"] = ServerFolderTexts.SystemFolder }))
                : Serialize(new SaveSettingsReply(settings));
        };
        ctx.Downloads[LicensePlatesMethods.ExportLog] = _ => Encoding.UTF8.GetBytes("Time,Camera,Plate\n");
        ctx.Downloads[LicensePlatesMethods.ExportPlates] = _ => Encoding.UTF8.GetBytes("plate,name,tags\n");
        return ctx;
    }

    public static IReadOnlyList<ImportRow> ImportRows() =>
    [
        new(2, "W12345A", "Anna Huber", ["Garage Nord", "Staff"], null, null, false, ImportRowStatus.Unchanged, null),
        new(3, "G8899LK", "Hofer delivery", ["Delivery"], new DateTime(2026, 12, 1, 6, 0, 0), new DateTime(2026, 12, 24, 18, 0, 0), true, ImportRowStatus.New, null),
        new(4, "S7788P", "Peter Lang", ["Garage Süd"], null, null, false, ImportRowStatus.New, null),
        new(5, "W12", "Short", [], null, null, false, ImportRowStatus.Refused, "A plate has 2 to 12 characters."),
        new(6, "L5544MM", "Maria Moser", ["Visitors"], null, new DateTime(2026, 12, 6, 20, 0, 0), true, ImportRowStatus.New, null),
        new(7, "KÄ12", "Umlaut", [], null, null, false, ImportRowStatus.Refused, "Use only letters, digits and *."),
        new(8, "L8820BB", "Lukas Berger", ["Garage Süd", "Staff"], null, null, false, ImportRowStatus.Changed, null),
    ];

    public static string Serialize<T>(T value) => PluginJson.Serialize(value, LicensePlatesJson.Options);

    public static T Parse<T>(string? json) => PluginJson.Parse<T>(json, LicensePlatesJson.Options);

    /// <summary>The page against the sample server, shown (Activate), its first reads done.</summary>
    public static async Task<(LicensePlatesViewModel Page, FakePageContext Ctx, FakeDialogs Dialogs)> OpenAsync(FakePageContext? ctx = null)
    {
        ctx ??= Context();
        var page = new LicensePlatesViewModel(ctx, Clock()) { Dialogs = new FakeDialogs() };
        page.Log.Decode = bytes => null;
        page.Activate();
        await SettleAsync(page);
        return (page, ctx, (FakeDialogs)page.Dialogs!);
    }

    /// <summary>Waits for the page's reads of now (tags, cameras, plates, log, status).</summary>
    public static async Task SettleAsync(LicensePlatesViewModel page)
    {
        for (var i = 0; i < 3; i++)
        {
            await page.ListsTask;
            await page.TagsTask;
            await page.Cameras.ReloadTask;
            await page.Plates.ReloadTask;
            await page.Log.LoadTask;
            await page.Log.StatusTask;
            await page.Stats.LoadTask;
        }
    }
}
