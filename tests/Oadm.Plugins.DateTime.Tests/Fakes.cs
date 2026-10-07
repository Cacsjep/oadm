using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>param.cgi list text into the dictionary shape IVapixClient.ListParametersAsync returns (no "root.").</summary>
    public static Dictionary<string, string> Parameters(string name) =>
        Read(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].StartsWith("root.", StringComparison.Ordinal) ? p[0][5..] : p[0], p => p[1]);

    public const string DateTimeInfo = "time-service-getDateTimeInfo-P3265-V-12.11.json";
    public const string NtpInfo = "ntp-getNTPInfo-P3265-V-12.11.json";
    public const string ParamTime = "param-Time-P3265-V-12.11.txt";
    public const string Error4000 = "time-service-error-4000-P3265-V-12.11.json";
    public const string Error4001 = "time-service-error-4001-P3265-V-12.11.json";
    public const string TimeZoneList = "rest-time-v2-getTimeZoneList-P3265-V-12.11.txt";

    /// <summary>What 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77) lists.</summary>
    public static readonly IReadOnlyList<DeviceApi> Modern =
    [
        new("time-service", "1.1"), new("ntp", "1.5"), new("param-cgi", "1.0"), new("basic-device-info", "1.3"),
    ];

    /// <summary>AXIS OS 9.30 - 10.x style: Time API and an NTP API without NTS.</summary>
    public static readonly IReadOnlyList<DeviceApi> NoNts = [new("time-service", "1.0"), new("ntp", "1.0"), new("param-cgi", "1.0")];

    /// <summary>Firmware before the Time and NTP APIs: param.cgi only.</summary>
    public static readonly IReadOnlyList<DeviceApi> LegacyOnly = [new("param-cgi", "1.0")];

    /// <summary>Time API present, NTP only through param.cgi (AXIS OS 9.x before ntp).</summary>
    public static readonly IReadOnlyList<DeviceApi> TimeServiceOnly = [new("time-service", "1.0"), new("param-cgi", "1.0")];

    /// <summary>A future major version only: a different API.</summary>
    public static readonly IReadOnlyList<DeviceApi> FutureMajor = [new("time-service", "2.0"), new("ntp", "2.0")];

    /// <summary>The device clock of the fixture (getDateTimeInfo dateTime).</summary>
    public static readonly DateTimeOffset DeviceNow = new(2026, 10, 7, 16, 24, 1, TimeSpan.Zero);
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

/// <summary>Recorded request sent through IVapixClient.SendAsync.</summary>
internal sealed record SentRequest(string Path, string ContentType, string Body)
{
    public string? Method => Body.StartsWith('{') ? JsonNode.Parse(Body)?["method"]?.GetValue<string>() : null;

    public JsonNode? Params => Body.StartsWith('{') ? JsonNode.Parse(Body)?["params"] : null;
}

/// <summary>
/// A stateful fake device: getDateTimeInfo / getNTPInfo / param.cgi answer from the recorded 10.0.0.48 fixtures,
/// writes change the state like the device does (so the verify steps see them), every request is recorded.
/// </summary>
internal sealed class FakeTimeVapix : IVapixClient
{
    private readonly FakeTimeProvider _time;
    private TimeSpan _clockOffset;

    public FakeTimeVapix(FakeTimeProvider time)
    {
        _time = time;
        DateTimeInfo = JsonNode.Parse(Fixture.Read(Fixture.DateTimeInfo))!["data"]!.AsObject();
        NtpClient = JsonNode.Parse(Fixture.Read(Fixture.NtpInfo))!["data"]!["client"]!.AsObject();
        Parameters = Fixture.Parameters(Fixture.ParamTime);
        _clockOffset = Fixture.DeviceNow - time.GetUtcNow();
    }

    public Uri BaseAddress { get; init; } = new("http://10.0.0.48/");

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = Fixture.Modern;

    public JsonObject DateTimeInfo { get; }

    public JsonObject NtpClient { get; }

    public Dictionary<string, string> Parameters { get; }

    public List<SentRequest> Sent { get; } = [];

    /// <summary>Requests that change something (set*, param.cgi update).</summary>
    public List<SentRequest> Writes => [.. Sent.Where(r => r.Method?.StartsWith("set", StringComparison.Ordinal) == true || r.Body.StartsWith("action=update", StringComparison.Ordinal))];

    /// <summary>Custom raw answer for a request (status, body); null = the fake device answers.</summary>
    public Func<SentRequest, (HttpStatusCode Status, string Body)?> Respond { get; set; } = _ => null;

    /// <summary>False: writes are acknowledged but not applied (verify sees the old state).</summary>
    public bool ApplyWrites { get; set; } = true;

    public int ParameterReads { get; private set; }

    public DateTimeOffset DeviceUtc => _time.GetUtcNow() + _clockOffset;

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult(ApiList);

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
        Task.FromResult(new BasicDeviceInfo("ACCC8E000001", "P3265-V", null, null, "12.11.77", null, null));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        ParameterReads++;
        var prefixes = groups.ToList();
        IReadOnlyDictionary<string, string> result = Parameters
            .Where(p => prefixes.Any(g => p.Key == g || p.Key.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult(result);
    }

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("The date and time plugin must never restart a device.");

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var sent = new SentRequest(request.RequestUri!.OriginalString, request.Content?.Headers.ContentType?.MediaType ?? string.Empty, body);
        Sent.Add(sent);
        if (Respond(sent) is { } custom)
        {
            return new HttpResponseMessage(custom.Status) { Content = new StringContent(custom.Body, Encoding.UTF8, "application/json") };
        }

        var text = sent.Path.EndsWith("param.cgi", StringComparison.Ordinal) ? Param(body) : Json(sent);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private string Param(string body)
    {
        if (ApplyWrites)
        {
            foreach (var pair in body.Split('&').Skip(1).Select(p => p.Split('=', 2)))
            {
                Parameters[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
            }
        }

        return "OK";
    }

    private string Json(SentRequest sent)
    {
        var request = JsonNode.Parse(sent.Body)!;
        var method = request["method"]!.GetValue<string>();
        var version = request["apiVersion"]?.GetValue<string>() ?? "1.0";
        var p = request["params"];
        JsonNode? data = null;
        switch (method)
        {
            case "getDateTimeInfo":
                var info = DateTimeInfo.DeepClone().AsObject();
                info["dateTime"] = DeviceUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                data = info;
                break;
            case "getNTPInfo":
                data = new JsonObject { ["client"] = NtpClient.DeepClone() };
                break;
            case "setTimeZone" when ApplyWrites:
                DateTimeInfo.Remove("posixTimeZone");
                DateTimeInfo.Remove("dstEnabled");
                DateTimeInfo["timeZone"] = p!["timeZone"]!.GetValue<string>();
                break;
            case "setPosixTimeZone" when ApplyWrites:
                DateTimeInfo.Remove("timeZone");
                DateTimeInfo["posixTimeZone"] = p!["posixTimeZone"]!.GetValue<string>();
                DateTimeInfo["dstEnabled"] = p["enableDst"]!.GetValue<bool>();
                break;
            case "setDateTime" when ApplyWrites:
                var set = DateTimeOffset.Parse(p!["dateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                _clockOffset = set - _time.GetUtcNow();
                break;
            case "setNTPClientConfiguration" when ApplyWrites:
                foreach (var (key, value) in p!.AsObject())
                {
                    NtpClient[key] = value?.DeepClone();
                }

                NtpClient["synced"] = false;
                break;
        }

        var answer = new JsonObject { ["apiVersion"] = version, ["context"] = "oadm", ["method"] = method };
        answer["data"] = data ?? new JsonObject();
        return answer.ToJsonString();
    }
}

internal sealed class RecordingContext : ITaskExecutionContext, ITaskQueryContext
{
    public RecordingContext(IVapixClient vapix)
    {
        Vapix = vapix;
        Steps = new TaskStepList(onWarning: Warnings.Add);
    }

    /// <summary>The steps exactly as the server's task engine records them.</summary>
    public TaskStepList Steps { get; }

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public Guid TaskId { get; } = Guid.NewGuid();

    public IVapixClient Vapix { get; }

    public ILogger Logger { get; } = NullLogger.Instance;

    public ICorePlugin? Owner => null;

    public IUploadedFiles Files => throw new NotSupportedException();

    public List<string> Warnings { get; } = [];

    public List<(TaskLogLevel Level, string Message)> LogEntries { get; } = [];

    public void ReportProgress(int percent, string? message = null)
    {
    }

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => LogEntries.Add((level, message));
}
