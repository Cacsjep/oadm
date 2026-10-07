using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Web;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap.Tests;

internal sealed class FakeDevice : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Serial { get; init; } = "B8A44F631339";
    public string Address { get; init; } = "10.0.0.48";
    public string? HostName { get; init; }
    public string? Model { get; init; } = "P3265-V";
    public string? FirmwareVersion { get; init; } = "12.11.77";
    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;
    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;
    public bool HasVideo => DeviceCategories.HasVideo(Category);
    public IReadOnlyList<DeviceApi> Apis { get; init; } = [new("application", "1.0"), new("packagemanager", "1.4")];
}

/// <summary>An Axis device with the Application API, kept in memory. Records every write.</summary>
internal sealed class FakeAcapDevice : IVapixClient
{
    public string Architecture { get; set; } = "aarch64";
    public string Firmware { get; set; } = "12.11.77";
    public string EmbeddedDevelopment { get; set; } = "2.18";
    public bool? AllowUnsigned { get; set; } = true;
    public List<DeviceApi> Apis { get; } = [new("application", "1.0"), new("packagemanager", "1.4"), new("basic-device-info", "1.3")];
    public Dictionary<string, InstalledApplication> Apps { get; } = new(StringComparer.Ordinal);

    /// <summary>Every write request (upload/control) as "METHOD path?query".</summary>
    public List<string> Writes { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    public long UploadedBytes { get; private set; }
    public string? UploadedFileName { get; private set; }

    /// <summary>Overrides the upload answer, e.g. "Error: 2".</summary>
    public string? UploadReply { get; set; }

    /// <summary>Install the package but throw a timeout instead of answering (slow device).</summary>
    public bool UploadTimesOut { get; set; }

    /// <summary>The start command succeeds but the application stays stopped (e.g. missing license).</summary>
    public bool StartHasNoEffect { get; set; }

    public int ApiListCalls { get; private set; }

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    public void Add(string name, string version, string status = "Stopped", bool bundled = false, string vendor = "Acme") =>
        Apps[name] = new InstalledApplication { Name = name, NiceName = name + " nice", Vendor = vendor, Version = version, Status = status, Bundled = bundled, License = "None", SignatureStatus = "Unknown" };

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
        Task.FromResult(new BasicDeviceInfo("B8A44F631339", "P3265-V", "AXIS P3265-V", "AXIS P3265-V Dome Camera", Firmware, "931.11", Architecture, "Dome Camera"));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["Properties.EmbeddedDevelopment.Version"] = EmbeddedDevelopment });

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("Not expected.");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        ApiListCalls++;
        return Task.FromResult<IReadOnlyList<DeviceApi>>(Apis.ToList());
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var uri = new Uri(BaseAddress, request.RequestUri!);
        var query = HttpUtility.ParseQueryString(uri.Query);
        switch (uri.AbsolutePath)
        {
            case ApplicationApiClient.ListPath:
                return Text(ListXml());
            case ApplicationApiClient.ConfigPath:
                return AllowUnsigned is { } allow
                    ? Text($"<reply result=\"ok\">\n\t<param name=\"{query["name"]}\" value=\"{(allow ? "true" : "false")}\"/>\n</reply>")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            case ApplicationApiClient.UploadPath:
                Writes.Add("upload");
                return await UploadAsync(request, ct);
            case ApplicationApiClient.ControlPath:
                Writes.Add($"{query["action"]} {query["package"]}");
                return Text(Control(query["action"]!, query["package"]!));
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
        var part = Assert.Single(multipart);
        Assert.Equal("file", part.Headers.ContentDisposition!.Name!.Trim('"'));
        Assert.Equal("application/octet-stream", part.Headers.ContentType!.MediaType);
        UploadedFileName = part.Headers.ContentDisposition.FileName?.Trim('"');
        var bytes = await part.ReadAsByteArrayAsync(ct);
        UploadedBytes = bytes.Length;
        if (UploadReply is not null)
        {
            return Text(UploadReply);
        }

        var manifest = await EapReader.ReadAsync(new MemoryStream(bytes), ct);
        var wasRunning = Apps.TryGetValue(manifest.AppName, out var old) && old.IsRunning;
        Apps[manifest.AppName] = new InstalledApplication
        {
            Name = manifest.AppName,
            NiceName = manifest.FriendlyName,
            Vendor = manifest.Vendor,
            Version = manifest.Version,
            Status = wasRunning ? "Running" : "Stopped",
            License = "None",
        };
        if (UploadTimesOut)
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.", new TimeoutException());
        }

        return Text("OK");
    }

    private string Control(string action, string package)
    {
        if (!Apps.TryGetValue(package, out var app))
        {
            return "Error: 4";
        }

        switch (action)
        {
            case "start":
                if (app.IsRunning)
                {
                    return "Error: 6";
                }

                if (!StartHasNoEffect)
                {
                    Apps[package] = app with { Status = "Running" };
                }

                return "OK";
            case "stop":
                if (!app.IsRunning)
                {
                    return "Error: 7";
                }

                Apps[package] = app with { Status = "Stopped" };
                return "OK";
            case "remove":
                Apps.Remove(package);
                return "OK";
            default:
                return "Error: 10";
        }
    }

    private string ListXml()
    {
        var sb = new StringBuilder("<reply result=\"ok\">\n");
        foreach (var a in Apps.Values)
        {
            sb.Append(CultureInfoInvariant($" <application Name=\"{E(a.Name)}\" NiceName=\"{E(a.NiceName)}\" Vendor=\"{E(a.Vendor)}\" Version=\"{E(a.Version)}\" Bundled=\"{(a.Bundled ? "Yes" : "No")}\" Status=\"{E(a.Status)}\" License=\"{E(a.License)}\" SignatureStatus=\"{E(a.SignatureStatus)}\" />\n"));
        }

        return sb.Append("</reply>").ToString();
    }

    private static string CultureInfoInvariant(FormattableString s) => FormattableString.Invariant(s);

    private static string E(string? s) => SecurityElement.Escape(s ?? string.Empty);

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

internal sealed class InMemoryUploadedFiles : IUploadedFiles
{
    private readonly Dictionary<string, (UploadedFile File, byte[] Data)> _files = [];

    public UploadedFile Add(string name, byte[] data)
    {
        var file = new UploadedFile(Guid.NewGuid().ToString("N"), name, data.Length, Convert.ToHexString(SHA256.HashData(data)));
        _files[file.Id] = (file, data);
        return file;
    }

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(fileId, out var f) ? f.File : null);

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(_files[fileId].Data, writable: false));
}

internal sealed class RecordingContext(IVapixClient vapix, IUploadedFiles? files = null) : ITaskExecutionContext, ITaskQueryContext
{
    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix { get; } = vapix;
    public ILogger Logger { get; } = NullLogger.Instance;
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files { get; } = files ?? new InMemoryUploadedFiles();
    public List<(int Percent, string? Message)> Progress { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<(TaskLogLevel Level, string Message)> Logs { get; } = [];

    public void ReportProgress(int percent, string? message = null) => Progress.Add((percent, message));

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => Logs.Add((level, message));
}
