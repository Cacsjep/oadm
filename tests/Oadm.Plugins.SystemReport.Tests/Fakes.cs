using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SystemReport.Tests;

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
}

internal sealed class FakeRepository(params IDeviceInfo[] devices) : IDeviceRepository
{
    public List<IDeviceInfo> Devices { get; } = [.. devices];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Devices]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id));
}

/// <summary>
/// A device answering serverreport.cgi with a small synthetic report ZIP (serverreport_cgi.txt, plus
/// serverreport_image.jpg for zip_with_image, like AXIS OS 12.11), or what the test sets. Every other request fails the
/// test: the plugin is read-only.
/// </summary>
internal sealed class FakeCamera : IVapixClient
{
    private int _running;

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    /// <summary>Overrides the answer.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }

    public TimeSpan Delay { get; set; }

    /// <summary>Extra bytes of padding in the report (large answers).</summary>
    public int Padding { get; set; }

    public List<string> Requests { get; } = [];

    public int MaxConcurrent { get; private set; }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new InvalidOperationException("not used");

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new InvalidOperationException("not used");

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("Read-only plugin must never restart.");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceApi>>([]);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
        }

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("axis-cgi/serverreport.cgi?mode=", request.RequestUri!.OriginalString, StringComparison.Ordinal);
        Assert.True(request.Options.TryGetValue(VapixRequestOptions.StreamResponse, out var stream) && stream, "the report is streamed to disk");
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

            var withImage = request.RequestUri.OriginalString.EndsWith("zip_with_image", StringComparison.Ordinal);
            return Zip(Report(withImage, Padding));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public static HttpResponseMessage Zip(byte[] data)
    {
        // Unknown length like the device's chunked answer.
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(data)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return response;
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string text, string contentType = "text/plain")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    /// <summary>A synthetic server report ZIP (no device data).</summary>
    public static byte[] Report(bool withImage, int padding = 0)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var text = zip.CreateEntry("serverreport_cgi.txt");
            using (var writer = new StreamWriter(text.Open()))
            {
                writer.Write("Server report for: TEST\nProduct: AXIS P3265-V\n");
            }

            if (withImage)
            {
                var image = zip.CreateEntry("serverreport_image.jpg", CompressionLevel.NoCompression);
                using var s = image.Open();
                s.Write([0xFF, 0xD8, 0xFF, 0xD9]);
            }

            if (padding > 0)
            {
                var pad = zip.CreateEntry("padding.bin", CompressionLevel.NoCompression);
                using var s = pad.Open();
                var bytes = new byte[padding];
                Random.Shared.NextBytes(bytes);
                s.Write(bytes);
            }
        }

        return memory.ToArray();
    }

    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
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

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oadm-system-report-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
