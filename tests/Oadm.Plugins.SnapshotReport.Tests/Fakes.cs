using System.Net;
using System.Net.Http.Headers;

using Avalonia.Media;

using Oadm.Client.Api;
using Oadm.Plugins.SnapshotReport.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport.Tests;

internal sealed class FakeDevice : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Serial { get; init; } = "B8A44F631339";
    public string Address { get; init; } = "10.0.0.48";
    public string? HostName { get; init; }
    public string? Model { get; init; } = "AXIS P3265-V";
    public string? FirmwareVersion { get; init; } = "12.11.77";
    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;
    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;
    public bool HasVideo => DeviceCategories.HasVideo(Category);
    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];
    public DateTime? CertNotAfterUtc { get; init; }
    public string? CertTrustName { get; init; }
}

internal sealed class FakeRepository(params IDeviceInfo[] devices) : IDeviceRepository
{
    public List<IDeviceInfo> Devices { get; } = [.. devices];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Devices]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id));
}

/// <summary>A camera with video sources that answers image.cgi with a generated JPEG, or what the test sets.</summary>
internal sealed class FakeCamera : IVapixClient
{
    private int _running;

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    public IReadOnlyList<VideoSource> Sources { get; set; } =
        [new VideoSource(1, "View Area 1", 0, [new(1920, 1080), new(1280, 720), new(640, 360)])];

    /// <summary>Overrides the image.cgi answer.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }

    /// <summary>Thrown by GetVideoSourcesAsync (e.g. a VAPIX 401).</summary>
    public Exception? SourcesError { get; set; }

    public TimeSpan Delay { get; set; }

    public List<string> Requests { get; } = [];

    public int MaxConcurrent { get; private set; }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("Read-only plugin must never restart.");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceApi>>([]);

    public Task<IReadOnlyList<VideoSource>> GetVideoSourcesAsync(CancellationToken ct) =>
        SourcesError is { } error ? Task.FromException<IReadOnlyList<VideoSource>>(error) : Task.FromResult(Sources);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
        }

        Assert.Equal(HttpMethod.Get, request.Method);
        var running = Interlocked.Increment(ref _running);
        lock (Requests)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, running);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            if (Handler is { } handler)
            {
                return await handler(request, ct);
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.OriginalString.Split('?')[1]);
            var size = query["resolution"]!.Split('x').Select(int.Parse).ToArray();
            var jpeg = FakeOadmApi.TestPicture(size[0], size[1], "camera " + query["camera"], "TEST", DateTimeOffset.UtcNow, 1);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

internal sealed class FakeVapixFactory : IVapixClientFactory
{
    public Dictionary<Guid, FakeCamera> Cameras { get; } = [];

    public FakeCamera Add(Guid id, FakeCamera? camera = null) => Cameras[id] = camera ?? new FakeCamera();

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        Cameras.TryGetValue(deviceId, out var camera)
            ? Task.FromResult<IVapixClient>(camera)
            : throw new KeyNotFoundException("no such device");
}

/// <summary>Page backend over the fake API (the same JSON the real plugin produces).</summary>
internal sealed class FakeApiContext(FakeOadmApi api) : ICorePluginClientContext
{
    public List<string> Calls { get; } = [];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(method);
        }

        return api.InvokeCorePluginAsync(SnapshotReportPluginInfo.PluginId, method, payloadJson, ct);
    }
}

/// <summary>Page backend calling the real plugin in-process.</summary>
internal sealed class PluginContext(SnapshotReportPlugin plugin) : ICorePluginClientContext
{
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);
}

/// <summary>UI fake: decodes nothing, records previews and exports.</summary>
internal sealed class FakeUi : ISnapshotReportUi
{
    public List<SnapshotTileViewModel> Previews { get; } = [];
    public List<ExportReportViewModel> Exports { get; } = [];
    public int Decoded { get; private set; }

    public Func<ExportReportViewModel, Task>? OnExport { get; set; }

    public IImage? Decode(byte[] jpeg, int decodeWidth)
    {
        Assert.True(jpeg.Length > 100);
        Decoded++;
        return null;
    }

    public void ShowPreview(SnapshotTileViewModel tile) => Previews.Add(tile);

    public Task ShowExportAsync(ExportReportViewModel dialog)
    {
        Exports.Add(dialog);
        return OnExport?.Invoke(dialog) ?? Task.CompletedTask;
    }
}

internal sealed class MemorySavePicker : IReportSavePicker
{
    public MemoryStream Written { get; } = new();
    public string? Suggested { get; private set; }
    public bool Cancel { get; set; }

    public Task<ReportTarget?> PickAsync(string suggestedFileName, string? folder)
    {
        Suggested = suggestedFileName;
        return Task.FromResult(Cancel ? null : new ReportTarget(new NonClosingStream(Written), "C:/reports/" + suggestedFileName, "C:/reports"));
    }

    /// <summary>Keeps the memory stream readable after the view model disposed the target.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

internal static class TempFiles
{
    public static string NewSettingsPath() => Path.Combine(Path.GetTempPath(), "oadm-snapshot-tests", Guid.NewGuid().ToString("N"), "export.json");
}
