using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Tests;

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The whole export of 10.0.0.48.</summary>
    public static string Export => Read("export-all.json");

    public static string Discover => Read("discover.json");
}

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

/// <summary>
/// A camera with the configuration API of AXIS OS 12.11: <c>$export</c> answers its current configuration (starting from the
/// recorded 10.0.0.48 export), <c>/config/discover</c> the recorded list, and <c>$import</c> merges the sent area into the
/// configuration. Everything else fails the test.
/// </summary>
internal sealed class ConfigCamera : IVapixClient
{
    public ConfigCamera()
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        Data = (JsonObject)export["data"]!.DeepClone();
    }

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    public JsonObject Data { get; private set; }

    public string ProdNbr { get; set; } = "P3265-V";

    public string Version { get; set; } = "12.11.77";

    /// <summary>The <c>/config/discover</c> answer.</summary>
    public string DiscoverBody { get; set; } = Fixtures.Discover;

    /// <summary>Status of <c>$export</c> and <c>/config/discover</c> (404 = the feature is off).</summary>
    public HttpStatusCode ConfigStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Answer for an import of one area instead of merging it: (status, body); null = merge and answer success.</summary>
    public Func<string, JsonNode, (HttpStatusCode Status, string Body)?>? ImportAnswer { get; set; }

    /// <summary>Values the device keeps after an import (path in the area, e.g. "Image.DateFormat"): the verify step sees a difference.</summary>
    public Dictionary<string, HashSet<string>> Ignores { get; } = [];

    /// <summary>Imported areas: (key, data as sent, query string).</summary>
    public List<(string Key, JsonNode Data, string Query)> Imports { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>Requests that fail with a connection reset before the device answers again (web server reload).</summary>
    public int ResetNext { get; set; }

    public int Exports { get; private set; }

    /// <summary>Called with "METHOD path?query" before each request; an exception it returns is thrown (timeout, lost connection).</summary>
    public Func<string, Exception?>? Fail { get; set; }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
        Task.FromResult(new BasicDeviceInfo("B8A44F631339", ProdNbr, ProdNbr, "AXIS " + ProdNbr + " Dome Camera", Version, "9A3", "aarch64", "Dome Camera", "Axis Artpec-8"));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new InvalidOperationException("not used");

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("not used");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeviceApi>>([]);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = request.RequestUri!.OriginalString;
        Requests.Add(request.Method + " " + path);
        if (Fail?.Invoke(request.Method + " " + path) is { } failure)
        {
            throw failure;
        }

        if (ResetNext > 0)
        {
            ResetNext--;
            throw new HttpRequestException("An error occurred while sending the request.", new IOException("Connection reset"));
        }

        var query = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        path = path.Split('?')[0];
        if (request.Method == HttpMethod.Get && path == "/config/rest/$export")
        {
            Exports++;
            if (ConfigStatus != HttpStatusCode.OK)
            {
                return Answer(ConfigStatus, "<html>Not Found</html>");
            }

            var export = new JsonObject
            {
                ["status"] = "success",
                ["data"] = Data.DeepClone(),
                ["metadata"] = new JsonObject { ["fwVersion"] = Version, ["prodNbr"] = ProdNbr },
            };
            return Answer(HttpStatusCode.OK, export.ToJsonString());
        }

        if (request.Method == HttpMethod.Get && path == "/config/discover")
        {
            return ConfigStatus != HttpStatusCode.OK ? Answer(ConfigStatus, "<html>Not Found</html>") : Answer(HttpStatusCode.OK, DiscoverBody);
        }

        if (request.Method == HttpMethod.Patch && path == "/config/rest/$import")
        {
            var body = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var data = (JsonObject)body["data"]!;
            Assert.Single(data);
            var (key, value) = data.Single();
            Imports.Add((key, value!.DeepClone(), query));
            if (ImportAnswer?.Invoke(key, value) is { } answer)
            {
                return Answer(answer.Status, answer.Body);
            }

            Merge(Data, key, value, Ignores.GetValueOrDefault(key) ?? [], string.Empty);
            return Answer(HttpStatusCode.OK, """{"status":"success"}""");
        }

        throw new InvalidOperationException("Unexpected request " + request.Method + " " + path);
    }

    private static void Merge(JsonObject parent, string name, JsonNode? value, HashSet<string> ignores, string path)
    {
        var current = parent[name];
        if (value is JsonObject obj && current is JsonObject target)
        {
            foreach (var (child, childValue) in obj)
            {
                Merge(target, child, childValue, ignores, path.Length == 0 ? child : path + "." + child);
            }

            return;
        }

        // Parameter collections merge item by item, matched by groupId (as the device does).
        if (value is JsonArray items && current is JsonArray existing && items.All(i => i is JsonObject o && o["groupId"] is not null))
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                var id = (string?)item["groupId"];
                var match = existing.OfType<JsonObject>().FirstOrDefault(o => (string?)o["groupId"] == id);
                if (match is null)
                {
                    existing.Add(item.DeepClone());
                    continue;
                }

                foreach (var (child, childValue) in item)
                {
                    Merge(match, child, childValue, ignores, path + "." + id + "." + child);
                }
            }

            return;
        }

        if (ignores.Contains(path))
        {
            return;
        }

        parent[name] = value?.DeepClone();
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Uploaded files in memory.</summary>
internal sealed class MemoryUploads : IUploadedFiles
{
    private readonly Dictionary<string, (string Name, byte[] Data)> _files = [];

    public string Add(string name, byte[] data)
    {
        var id = Guid.NewGuid().ToString("N");
        _files[id] = (name, data);
        return id;
    }

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(fileId, out var file) ? new UploadedFile(fileId, file.Name, file.Data.Length, "00") : null);

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        _files.TryGetValue(fileId, out var file) ? Task.FromResult<Stream>(new MemoryStream(file.Data, writable: false)) : throw new FileNotFoundException(fileId);
}

/// <summary>An upload that is removed between <see cref="FindAsync"/> and <see cref="OpenReadAsync"/> (retention, quota).</summary>
internal sealed class VanishingUploads : IUploadedFiles
{
    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult<UploadedFile?>(new UploadedFile(fileId, "lobby.json", 1, "00"));

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        throw new FileNotFoundException($"Uploaded file {fileId} does not exist (it may have expired).", fileId);
}

/// <summary>Stands for the host's exception of a changed device certificate (plugins know it only by name).</summary>
internal sealed class CertificateChangedException(string message) : Exception(message);

internal sealed class RecordingContext : ITaskExecutionContext
{
    public RecordingContext(IVapixClient vapix, IUploadedFiles files)
    {
        Vapix = vapix;
        Files = files;
        Steps = new TaskStepList(onWarning: ReportWarning);
    }

    public TaskStepList Steps { get; }
    public List<string> Warnings { get; } = [];
    public List<(TaskLogLevel Level, string Message)> Logs { get; } = [];
    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix { get; }
    public ILogger Logger => NullLogger.Instance;
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files { get; }

    public void ReportProgress(int percent, string? message = null)
    {
    }

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => Logs.Add((level, message));
}

internal sealed class QueryContext(IVapixClient vapix) : ITaskQueryContext
{
    public IVapixClient Vapix { get; } = vapix;
    public ILogger Logger => NullLogger.Instance;
}
