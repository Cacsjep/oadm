using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Firmware.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static readonly DeviceApi Fwmgr110 = new("fwmgr", "1.10", "Firmware Management", "official");

    public static IReadOnlyList<DeviceApi> RecordedApis()
    {
        using var doc = JsonDocument.Parse(Read("apidiscovery-subset-p3265v.json"));
        return [.. doc.RootElement.GetProperty("data").GetProperty("apiList").EnumerateArray()
            .Select(a => new DeviceApi(a.GetProperty("id").GetString()!, a.GetProperty("version").GetString()!, a.GetProperty("name").GetString(), a.GetProperty("status").GetString()))];
    }

    /// <summary>Synthetic AXIS OS-like image: non-zero, non-archive header, deterministic content.</summary>
    public static byte[] Image(int size = 2 * 1024 * 1024, byte seed = 7)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        data[0] = 0xA5;
        data[1] = 0x5A;
        return data;
    }
}

internal sealed class FakeDevice(DeviceStatus status = DeviceStatus.Ok, string model = "P3265-V", string version = "11.11.160", params DeviceApi[] apis) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Serial => "B8A44F631339";
    public string Address { get; init; } = "10.0.0.48";
    public string? HostName => null;
    public string? Model { get; } = model;
    public string? FirmwareVersion { get; } = version;
    public DeviceStatus Status { get; } = status;
    public DeviceCategory Category => DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<DeviceApi> Apis { get; } = apis.Length == 0 ? [Fixture.Fwmgr110] : apis;
}

internal sealed class FakeFiles : IUploadedFiles
{
    private readonly Dictionary<string, (UploadedFile File, byte[] Data)> _files = [];

    public int Opens { get; private set; }

    public UploadedFile Add(string name, byte[] data)
    {
        var file = new UploadedFile(Guid.NewGuid().ToString("N"), name, data.Length, Convert.ToHexString(SHA256.HashData(data)));
        _files[file.Id] = (file, data);
        return file;
    }

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(fileId, out var f) ? f.File : null);

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct)
    {
        Opens++;
        return Task.FromResult<Stream>(new MemoryStream(_files[fileId].Data, writable: false));
    }
}

internal sealed class RecordingContext(IVapixClient vapix, IUploadedFiles files) : ITaskExecutionContext
{
    public List<(int Percent, string? Message)> Reports { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<(TaskLogLevel Level, string Message)> Logs { get; } = [];
    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix { get; } = vapix;
    public ILogger Logger => NullLogger.Instance;
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files { get; } = files;

    public void ReportProgress(int percent, string? message = null) => Reports.Add((percent, message));

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => Logs.Add((level, message));
}

internal sealed class QueryContext(IVapixClient vapix) : ITaskQueryContext
{
    public IVapixClient Vapix { get; } = vapix;
    public ILogger Logger => NullLogger.Instance;
}

/// <summary>What the fake device does after it accepted an upgrade.</summary>
internal enum AfterUpgrade
{
    /// <summary>Goes down for <see cref="FakeAxisDevice.DownProbes"/> probes, then answers with the new version.</summary>
    Restart,

    /// <summary>Goes down and comes back with the old version (the new image did not start).</summary>
    RollBack,

    /// <summary>Goes down and never answers again.</summary>
    NeverBack,

    /// <summary>Never goes down.</summary>
    NeverDown,
}

/// <summary>
/// Simulated Axis device behind <see cref="IVapixClient"/>: apidiscovery, basicdeviceinfo (also the
/// anonymous variant) and firmwaremanagement.cgi status / upgrade (multipart) / commit.
/// </summary>
internal sealed class FakeAxisDevice : IVapixClient
{
    public string ProdNbr { get; set; } = "P3265-V";
    public string Version { get; set; } = "11.11.160";
    public string NewVersion { get; set; } = "12.11.77";
    public IReadOnlyList<DeviceApi> Apis { get; set; } = Fixture.RecordedApis();
    public string StatusJson { get; set; } = Fixture.Read("fwmgr-status-p3265v-12.11.77.json");
    public int? UpgradeErrorCode { get; set; }
    public bool DropConnectionAfterUpload { get; set; }
    public int CommitFailures { get; set; }
    public AfterUpgrade After { get; set; } = AfterUpgrade.Restart;
    public int DownProbes { get; set; } = 3;

    public List<string> Methods { get; } = [];
    public string? UpgradeJson { get; private set; }
    public string? UploadedFileName { get; private set; }
    public byte[]? UploadedBytes { get; private set; }
    public string? UpgradeContentType { get; private set; }
    public int Commits { get; private set; }
    public int Probes { get; private set; }
    public bool Committed { get; private set; } = true;

    private bool _upgraded;
    private int _downCount;

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        Methods.Add("getAllProperties");
        return Task.FromResult(new BasicDeviceInfo("B8A44F631339", ProdNbr, "AXIS " + ProdNbr, null, Version, "931.11", "aarch64", "Dome Camera"));
    }

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("restart must not be used");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        Methods.Add("getApiList");
        return Task.FromResult(Apis);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.OriginalString;
        if (path.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
        {
            Methods.Add("getAllUnrestrictedProperties");
            return Probe();
        }

        Assert.Equal("axis-cgi/firmwaremanagement.cgi", path);
        Assert.Equal(HttpMethod.Post, request.Method);
        var contentType = request.Content!.Headers.ContentType!.MediaType;
        if (contentType == "multipart/form-data")
        {
            return await UpgradeAsync(request, ct);
        }

        var json = await request.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var method = doc.RootElement.GetProperty("method").GetString()!;
        Methods.Add(method);
        return method switch
        {
            "status" => Json(Committed ? StatusJson : UncommittedStatus()),
            "commit" => Commit(),
            _ => Json($$"""{"apiVersion":"1.10","method":"{{method}}","error":{"code":405,"message":"Unknown method in request."} }"""),
        };
    }

    private HttpResponseMessage Commit()
    {
        Commits++;
        if (CommitFailures > 0)
        {
            CommitFailures--;
            return Json("""{"apiVersion":"1.10","method":"commit","error":{"code":500,"message":"Internal error"}}""");
        }

        Committed = true;
        return Json($$"""{"apiVersion":"1.10","method":"commit","data":{"firmwareVersion":"{{Version}}"} }""");
    }

    private string UncommittedStatus() =>
        $$"""{"apiVersion":"1.10","method":"status","data":{"activeFirmwareVersion":"{{Version}}","inactiveFirmwareVersion":"11.11.160","isCommited":false,"timeToRollback":1700} }""";

    private async Task<HttpResponseMessage> UpgradeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Methods.Add("upgrade");
        UpgradeContentType = request.Content!.Headers.ContentType!.ToString();
        var parts = await Multipart.ParseAsync(request.Content, ct);
        Assert.Equal(2, parts.Count);
        Assert.Equal("json", parts[0].Name);
        Assert.Equal("file", parts[1].Name);
        UpgradeJson = Encoding.UTF8.GetString(parts[0].Body);
        UploadedFileName = parts[1].FileName;
        UploadedBytes = parts[1].Body;
        if (UpgradeErrorCode is { } code)
        {
            return Json($$"""{"apiVersion":"1.10","context":"oadm","method":"upgrade","error":{"code":{{code}},"message":"Upgrade failed."} }""");
        }

        _upgraded = true;
        using var doc = JsonDocument.Parse(UpgradeJson);
        Committed = doc.RootElement.GetProperty("params").GetProperty("autoCommit").GetString() != "never";
        if (DropConnectionAfterUpload)
        {
            throw new HttpRequestException("The response ended prematurely.");
        }

        return Json($$"""{"apiVersion":"1.10","context":"oadm","method":"upgrade","data":{"firmwareVersion":"{{NewVersion}}"} }""");
    }

    private HttpResponseMessage Probe()
    {
        Probes++;
        if (!_upgraded || After == AfterUpgrade.NeverDown)
        {
            return Unrestricted(Version);
        }

        if (_downCount < DownProbes || After == AfterUpgrade.NeverBack)
        {
            _downCount++;
            throw new HttpRequestException("No route to host");
        }

        if (After == AfterUpgrade.Restart)
        {
            Version = NewVersion;
        }

        return Unrestricted(Version);
    }

    private HttpResponseMessage Unrestricted(string version) =>
        Json($$"""{"apiVersion":"1.3","data":{"propertyList":{"ProdNbr":"{{ProdNbr}}","Version":"{{version}}","SerialNumber":"B8A44F631339"} } }""");

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>Minimal multipart/form-data parser for assertions (reads the serialized request body).</summary>
internal static class Multipart
{
    public sealed record Part(string? Name, string? FileName, string? ContentType, byte[] Body, string RawHeaders);

    public static async Task<List<Part>> ParseAsync(HttpContent content, CancellationToken ct)
    {
        var contentType = content.Headers.ContentType!;
        var boundary = contentType.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        return Parse(ms.ToArray(), boundary);
    }

    public static List<Part> Parse(byte[] body, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<Part>();
        var pos = IndexOf(body, delimiter, 0);
        Assert.True(pos >= 0, "boundary not found");
        while (true)
        {
            pos += delimiter.Length;
            if (body.AsSpan(pos).StartsWith("--"u8))
            {
                break;
            }

            pos += 2; // CRLF
            var headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), pos);
            var headers = Encoding.ASCII.GetString(body, pos, headerEnd - pos);
            var bodyStart = headerEnd + 4;
            var next = IndexOf(body, delimiter, bodyStart);
            var bodyEnd = next - 2; // CRLF before the delimiter
            parts.Add(new Part(Header(headers, "name"), Header(headers, "filename"), ContentType(headers), body[bodyStart..bodyEnd], headers));
            pos = next;
        }

        return parts;
    }

    private static string? Header(string headers, string param)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var piece in line.Split(';').Select(p => p.Trim()))
            {
                if (piece.StartsWith(param + "=", StringComparison.Ordinal))
                {
                    return piece[(param.Length + 1)..].Trim('"');
                }
            }
        }

        return null;
    }

    private static string? ContentType(string headers) =>
        headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?["Content-Type:".Length..].Trim();

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var i = haystack.AsSpan(start).IndexOf(needle);
        return i < 0 ? -1 : start + i;
    }
}
