using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.EdgeRecordingExport.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(EdgeRecordingInfo.PluginId, EdgeRecordingInfo.DisplayName, view, hasOwnCards: true),
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

    public static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Pump();
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

internal sealed record TestDevice(string Address, string? Model = "AXIS P3265-V", DeviceStatus Status = DeviceStatus.Ok, DeviceCategory Category = DeviceCategory.Camera, params string[] TagNames) : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "B8A44F" + Random.Shared.Next(0, 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    public IReadOnlyList<DeviceApi> Apis => [];

    public IReadOnlyList<string> Tags => TagNames;
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

/// <summary>One recording of a <see cref="FakeCamera"/>: stop null while it records.</summary>
internal sealed record FakeRecording(string Id, DateTimeOffset Start, DateTimeOffset? Stop, string DiskId = "SD_DISK");

/// <summary>
/// A camera with edge storage that answers like the AXIS P3265-V (AXIS OS 12.11): the list cut to the range and paged
/// from 0, properties with 2 MB per 10 s, capabilities with MP4, the export as bytes with the camera's file name.
/// Everything else is refused (read-only: GET only).
/// </summary>
internal sealed class FakeCamera : IVapixClient
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    public List<FakeRecording> Recordings { get; } = [];

    public ConcurrentQueue<string> Requests { get; } = new();

    public bool LocalStorage { get; set; } = true;

    public bool ExportRecording { get; set; } = true;

    public bool OffersMp4 { get; set; } = true;

    /// <summary>An SD card in use (disks/list.cgi "OK"); false: like a camera without a card.</summary>
    public bool HasDisk { get; set; } = true;

    /// <summary>Attributes of the SD card in the disk list instead of the P3265-V's (e.g. <c>status="failed"</c>).</summary>
    public string? SdCard { get; set; }

    /// <summary>Recordings whose export answers HTTP 400 (deleted meanwhile).</summary>
    public HashSet<string> Missing { get; } = [];

    /// <summary>Bytes of the camera's estimate per second of recording (-1: no estimate).</summary>
    public long EstimateBytesPerSecond { get; set; } = 200 * 1024;

    /// <summary>Bytes an export sends per second of recording.</summary>
    public long ExportBytesPerSecond { get; set; } = 1000;

    public Uri BaseAddress => new("http://camera/");

    public static FakeCamera WithClips(int count, TimeSpan every, TimeSpan length)
    {
        var camera = new FakeCamera();
        for (var i = 0; i < count; i++)
        {
            var start = T0 + every * i;
            camera.Recordings.Add(new FakeRecording($"{start:yyyyMMdd_HHmmss}_{i:X4}_B8A44F631339", start, start + length));
        }

        return camera;
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        Assert.Equal([EdgeStorageRequests.PropertiesGroup], groups);
        Requests.Enqueue("param.cgi");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["Properties.LocalStorage.LocalStorage"] = LocalStorage ? "yes" : "no",
            ["Properties.LocalStorage.ExportRecording"] = ExportRecording ? "yes" : "no",
        });
    }

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal(HttpMethod.Get, request.Method); // read-only
        var uri = request.RequestUri!.OriginalString;
        Requests.Enqueue(uri);
        if (uri == Oadm.Plugins.EdgeStorage.Disks.EdgeDisks.ListUri)
        {
            return Task.FromResult(Xml(SdCard is { } card
                ? $"<root><disks><disk diskid=\"SD_DISK\" {card}/><disk diskid=\"NetworkShare\" status=\"disconnected\"/></disks></root>"
                : Recorded.Read(HasDisk ? "p3265-disks.xml" : "no-sd-card-disks.xml")));
        }

        var path = uri.Split('?')[0];
        var query = HttpUtility.ParseQueryString(uri.Contains('?', StringComparison.Ordinal) ? uri[(uri.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
        return Task.FromResult(path switch
        {
            EdgeStorageRequests.ListPath => List(query),
            EdgeStorageRequests.CapabilitiesPath => Xml($"<ExportRecordingResponse><CapabilitiesSuccess><ExportCapabilities ExportFormat=\"matroska\"/>{(OffersMp4 ? "<ExportCapabilities ExportFormat=\"mp4\"/>" : string.Empty)}</CapabilitiesSuccess></ExportRecordingResponse>"),
            EdgeStorageRequests.PropertiesPath => Properties(query),
            EdgeStorageRequests.ExportPath => Export(query, request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private HttpResponseMessage List(System.Collections.Specialized.NameValueCollection query)
    {
        var from = Time(query["starttime"]);
        var to = Time(query["stoptime"]);
        var matched = Recordings
            .Where(r => (from is null || (r.Stop ?? DateTimeOffset.MaxValue) >= from) && (to is null || r.Start <= to))
            .OrderBy(r => r.Start)
            .ToList();
        var skip = int.Parse(query["startatresultnumber"] ?? "0", CultureInfo.InvariantCulture);
        var take = int.Parse(query["maxnumberofresults"] ?? "1000", CultureInfo.InvariantCulture);
        var xml = new StringBuilder($"<?xml version=\"1.0\"?><root><recordings totalnumberofrecordings=\"{Recordings.Count}\" numberofrecordings=\"{matched.Count}\">");
        foreach (var r in matched.Skip(skip).Take(take))
        {
            var start = from is { } f && f > r.Start ? f : r.Start;
            var stop = r.Stop is { } s && to is { } t && t < s ? t : r.Stop;
            xml.Append(CultureInfo.InvariantCulture, $"<recording diskid=\"{r.DiskId}\" recordingid=\"{r.Id}\" starttime=\"{EdgeStorageRequests.Time(start)}\" stoptime=\"{(stop is { } e ? EdgeStorageRequests.Time(e) : string.Empty)}\" recordingtype=\"triggered\" recordingstatus=\"{(r.Stop is null ? "recording" : "completed")}\" source=\"1\"><video mimetype=\"video/x-h264\"/></recording>");
        }

        xml.Append("</recordings></root>");
        return Xml(xml.ToString());
    }

    private HttpResponseMessage Properties(System.Collections.Specialized.NameValueCollection query)
    {
        if (Recordings.FirstOrDefault(r => r.Id == query["recordingid"]) is not { } r)
        {
            return Xml("<ExportRecordingResponse><GeneralError><ErrorCode>110</ErrorCode><ErrorDescription>Recording not found.</ErrorDescription></GeneralError></ExportRecordingResponse>");
        }

        var kb = EstimateBytesPerSecond < 0 ? -1 : (long)(Seconds(r, query) * EstimateBytesPerSecond / 1024);
        return Xml($"<ExportRecordingResponse><PropertiesSuccess><ExportProperties RecordingId=\"{r.Id}\" ExportFormat=\"{query["exportformat"]}\" EstimatedFileSize=\"{kb}\"/></PropertiesSuccess></ExportRecordingResponse>");
    }

    private HttpResponseMessage Export(System.Collections.Specialized.NameValueCollection query, HttpRequestMessage request)
    {
        Assert.True(request.Options.TryGetValue(VapixRequestOptions.StreamResponse, out var stream) && stream);
        if (Recordings.FirstOrDefault(r => r.Id == query["recordingid"]) is not { } r || Missing.Contains(r.Id))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("<HTML><TITLE>400 Bad Request</TITLE></HTML>", Encoding.UTF8, "text/html") };
        }

        var start = Time(query["starttime"]) ?? r.Start;
        var stop = Time(query["stoptime"]) ?? r.Stop ?? T0.AddHours(1);
        var bytes = new byte[Math.Max(1, (long)(Seconds(r, query) * ExportBytesPerSecond))];
        bytes[0] = 7;
        var extension = query["exportformat"] == "mp4" ? "mp4" : "mkv";
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(extension == "mp4" ? "video/mp4" : "video/x-matroska");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = $"\"{start.ToOffset(TimeSpan.FromHours(2)):yyyyMMdd_HHmmss}_{stop.ToOffset(TimeSpan.FromHours(2)):yyyyMMdd_HHmmss}.{extension}\"" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static double Seconds(FakeRecording r, System.Collections.Specialized.NameValueCollection query)
    {
        var start = Time(query["starttime"]) ?? r.Start;
        var stop = Time(query["stoptime"]) ?? r.Stop ?? T0.AddHours(1);
        return Math.Max(0, (stop - start).TotalSeconds);
    }

    private static DateTimeOffset? Time(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
}

/// <summary>The cameras by device id; unknown ids are unreachable.</summary>
internal sealed class FakeCameras : IVapixClientFactory
{
    public ConcurrentDictionary<Guid, FakeCamera> Cameras { get; } = new();

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        Cameras.TryGetValue(deviceId, out var camera)
            ? Task.FromResult<IVapixClient>(camera)
            : Task.FromException<IVapixClient>(new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(10061)));
}

internal sealed class FakeContext(IDeviceRepository devices, IVapixClientFactory vapix) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => vapix;

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings => throw new NotSupportedException();

    public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events => null;

    public IDeviceEventStreams? EventStreams => null;

    public IFirewallRules? Firewall => null;
}

/// <summary>The plugin with fake devices and cameras, and the page context that calls it in process.</summary>
internal sealed class Rig
{
    public Rig()
    {
        Plugin.StartAsync(new FakeContext(Devices, Cameras), CancellationToken.None).GetAwaiter().GetResult();
        Page = new PageContext(this);
    }

    public FakeDevices Devices { get; } = new();

    public FakeCameras Cameras { get; } = new();

    public EdgeRecordingExportPlugin Plugin { get; } = new(new EdgeRecordingOptions { RequestTimeout = TimeSpan.FromSeconds(5) });

    public PageContext Page { get; }

    public TestDevice Add(FakeCamera? camera, string address, DeviceStatus status = DeviceStatus.Ok, DeviceCategory category = DeviceCategory.Camera, params string[] tags)
    {
        var device = new TestDevice(address, "AXIS P3265-V", status, category, tags);
        Devices.Items.Add(device);
        if (camera is not null)
        {
            Cameras.Cameras[device.Id] = camera;
        }

        return device;
    }
}

/// <summary>The page context against the plugin in process (Invoke and Download), with a switchable role.</summary>
internal sealed class PageContext(Rig rig) : ICorePluginClientContext
{
    public HashSet<string> Denied { get; } = [];

    public int Downloads;

    public IReadOnlyList<IDeviceInfo> Devices => rig.Devices.Items;

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? PermissionsChanged;

    public bool Has(string permission) => !Denied.Contains(permission);

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => rig.Plugin.InvokeAsync(method, payloadJson, ct);

    public async Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct)
    {
        Interlocked.Increment(ref Downloads);
        return await rig.Plugin.OpenDownloadAsync(method, payloadJson, ct);
    }
}

internal static class Recorded
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>A folder in the temp folder, deleted at the end.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oadm-edge-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
