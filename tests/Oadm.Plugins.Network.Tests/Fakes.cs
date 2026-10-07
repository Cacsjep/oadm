using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>param.cgi list text into the dictionary shape IVapixClient.ListParametersAsync returns (no "root.").</summary>
    public static Dictionary<string, string> Parameters(string name) =>
        Read(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].StartsWith("root.", StringComparison.Ordinal) ? p[0][5..] : p[0], p => p[1]);

    public const string GetNetworkInfo = "network-settings-getNetworkInfo-P3265-V-12.11.json";
    public const string ParamNetwork = "param-Network-P3265-V-12.11.txt";

    public static readonly IReadOnlyList<DeviceApi> Modern =
    [
        new("network-settings", "1.37"), new("param-cgi", "1.0"), new("basic-device-info", "1.3"),
    ];

    /// <summary>network-settings below 1.6: IPv6 on/off falls back to param.cgi.</summary>
    public static readonly IReadOnlyList<DeviceApi> NetworkSettings15 = [new("network-settings", "1.5"), new("param-cgi", "1.0")];

    public static readonly IReadOnlyList<DeviceApi> LegacyOnly = [new("param-cgi", "1.0")];

    public static readonly IReadOnlyList<DeviceApi> JsonOnly = [new("network-settings", "1.37")];
}

internal sealed record FakeDevice(Guid Id, string Address = "10.0.0.48", DeviceStatus Status = DeviceStatus.Ok, string Serial = "ACCC8E000001") : IDeviceInfo
{
    public string? HostName => null;

    public string? Model => "P3265-V";

    public string? FirmwareVersion => "12.11.77";

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis { get; init; } = Fixture.Modern;
}

/// <summary>Recorded write (or read) request sent through IVapixClient.SendAsync.</summary>
internal sealed record SentRequest(string Path, string ContentType, string Body);

/// <summary>
/// Fake device: getNetworkInfo answers from the recorded fixture, param.cgi lists from the recorded fixture,
/// writes are recorded and answered with success (or a configured error).
/// </summary>
internal sealed class FakeNetworkVapix : IVapixClient
{
    private int _pings;

    public Uri BaseAddress { get; } = new("http://10.0.0.48/");

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = Fixture.Modern;

    public string NetworkInfoJson { get; set; } = Fixture.Read(Fixture.GetNetworkInfo);

    public Dictionary<string, string> Parameters { get; set; } = Fixture.Parameters(Fixture.ParamNetwork);

    public List<SentRequest> Sent { get; } = [];

    /// <summary>Writes only (getNetworkInfo excluded).</summary>
    public List<SentRequest> Writes => Sent.Where(r => !r.Body.Contains("\"getNetworkInfo\"", StringComparison.Ordinal)).ToList();

    /// <summary>Called with the 1-based ping number; true = the device answers basicdeviceinfo.</summary>
    public Func<int, bool> Answers { get; set; } = _ => true;

    /// <summary>Response body for a write; null = success.</summary>
    public Func<SentRequest, string?> WriteResponse { get; set; } = _ => null;

    public int Pings => _pings;

    public int ApiListCalls { get; private set; }

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        ApiListCalls++;
        return Task.FromResult(ApiList);
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _pings);
        if (!Answers(n))
        {
            throw new HttpRequestException("Connection timed out");
        }

        return Task.FromResult(new BasicDeviceInfo("ACCC8E000001", "P3265-V", null, null, "12.11.77", null, null));
    }

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        var prefixes = groups.ToList();
        IReadOnlyDictionary<string, string> result = Parameters
            .Where(p => prefixes.Any(g => p.Key == g || p.Key.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult(result);
    }

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("The network plugin must never restart a device.");

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var sent = new SentRequest(request.RequestUri!.OriginalString, request.Content?.Headers.ContentType?.MediaType ?? string.Empty, body);
        Sent.Add(sent);
        string text;
        if (body.Contains("\"getNetworkInfo\"", StringComparison.Ordinal))
        {
            text = NetworkInfoJson;
        }
        else if (WriteResponse(sent) is { } custom)
        {
            text = custom;
        }
        else
        {
            text = sent.Path.EndsWith("param.cgi", StringComparison.Ordinal) ? "OK" : """{"apiVersion":"1.38","context":"oadm","method":"x","data":{}}""";
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

internal sealed class RecordingContext(IVapixClient vapix) : ITaskExecutionContext, ITaskQueryContext
{
    public Guid TaskId { get; } = Guid.NewGuid();

    public IVapixClient Vapix { get; } = vapix;

    public ILogger Logger { get; } = NullLogger.Instance;

    public ICorePlugin? Owner => null;

    public IUploadedFiles Files => throw new NotSupportedException();

    public List<(int Percent, string? Message)> Progress { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<(TaskLogLevel Level, string Message)> LogEntries { get; } = [];

    public void ReportProgress(int percent, string? message = null) => Progress.Add((percent, message));

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => LogEntries.Add((level, message));
}
