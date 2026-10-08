using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>The answers recorded read-only from 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77).</summary>
internal static class Fixtures
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Read(string name) => Cache.GetOrAdd(name, n => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", n)));

    public static IReadOnlyList<DeviceApi> ApiList { get; } =
        [.. JsonNode.Parse(Read("apidiscovery-getApiList.json"))!["data"]!["apiList"]!.AsArray()
            .Select(a => new DeviceApi(a!["id"]!.GetValue<string>(), a["version"]!.GetValue<string>(), a["name"]?.GetValue<string>(), a["status"]?.GetValue<string>()))];
}

internal sealed record TestDevice : IDeviceInfo
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
    public IReadOnlyList<DeviceApi> Apis { get; init; } = Fixtures.ApiList;
    public DateTime? CertNotAfterUtc { get; init; }
    public string? CertTrustName { get; init; }
    public bool? DhcpEnabled { get; init; } = true;
    public bool? HttpsEnabled { get; init; } = true;
    public bool? Dot1xEnabled { get; init; } = false;
}

/// <summary>
/// A camera serving the recorded answers of 10.0.0.48 per request path. Records every request and refuses anything that is
/// not a read: only GETs, and POSTs of pwdgrp.cgi action=get, ntp.cgi getNTPInfo, applications list.cgi / config.cgi
/// action=get and the SOAP GetWebServerTlsConfiguration (<see cref="Violations"/>).
/// </summary>
internal sealed class FakeCamera : IVapixClient
{
    private int _running;

    public Uri BaseAddress { get; } = new("https://10.0.0.48/");

    /// <summary>Answer texts per path (without the query and the leading slash), replacing the recorded ones.</summary>
    public Dictionary<string, string> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HTTP status per path (an error answer instead of the recorded one).</summary>
    public Dictionary<string, HttpStatusCode> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exception per path ("*" = every request), e.g. a socket error.</summary>
    public Dictionary<string, Exception> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that hang until cancelled.</summary>
    public HashSet<string> Hangs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = Fixtures.ApiList;

    public TimeSpan Delay { get; set; }

    public List<string> Requests { get; } = [];

    public List<string> Violations { get; } = [];

    public int MaxConcurrent { get; private set; }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException("The scan sends its own param.cgi list.");

    public Task RestartAsync(CancellationToken ct)
    {
        Record("RESTART");
        Violations.Add("restart");
        throw new InvalidOperationException("A read-only scan must never restart a device.");
    }

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        Record("POST axis-cgi/apidiscovery.cgi");
        return Task.FromResult(ApiList);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!.OriginalString.TrimStart('/');
        var path = uri.Split('?')[0];
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Record($"{request.Method} {uri}");
        CheckReadOnly(request.Method, path, uri, body);

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

            if (Hangs.Contains(path))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Throws.TryGetValue(path, out var error) || Throws.TryGetValue("*", out error))
            {
                throw error;
            }

            if (Statuses.TryGetValue(path, out var status))
            {
                return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.Unauthorized ? "<html><title>401 Unauthorized</title></html>" : string.Empty) };
            }

            var text = Answers.TryGetValue(path, out var answer) ? answer : Recorded(path, body);
            return text is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html><title>404 Not Found</title></html>") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static string? Recorded(string path, string body) => path.ToLowerInvariant() switch
    {
        "axis-cgi/param.cgi" => Fixtures.Read("paramcgi-hardening.txt"),
        "axis-cgi/pwdgrp.cgi" => Fixtures.Read("pwdgrp-get.txt"),
        "config/discover" => Fixtures.Read("config-discover.json"),
        "config/rest/user-management/v2" => Fixtures.Read("rest-user-management-v2-get.json"),
        "axis-cgi/ntp.cgi" => Fixtures.Read("ntp-getNTPInfo.json"),
        "axis-cgi/disks/list.cgi" => Fixtures.Read("disks-list.xml"),
        "axis-cgi/applications/list.cgi" => Fixtures.Read("applications-list.xml"),
        "axis-cgi/applications/config.cgi" => Fixtures.Read("applications-config-AllowUnsigned.xml"),
        "config/rest/firewall/v1" => Fixtures.Read("rest-firewall-v1-get.json"),
        "config/rest/lldp/v1" => Fixtures.Read("rest-lldp-v1-get.json"),
        "config/rest/snmp/v1" => Fixtures.Read("rest-snmp-v1-get.json"),
        "config/rest/oidcsetup/v1" => Fixtures.Read("rest-oidcsetup-v1-get.json"),
        "vapix/services" when body.Contains("GetWebServerTlsConfiguration", StringComparison.Ordinal) => Fixtures.Read("soap-GetWebServerTlsConfiguration.xml"),
        _ => null,
    };

    private void CheckReadOnly(HttpMethod method, string path, string uri, string body)
    {
        if (method == HttpMethod.Get)
        {
            return;
        }

        var allowed = method == HttpMethod.Post && path.ToLowerInvariant() switch
        {
            "axis-cgi/pwdgrp.cgi" => body.Trim() == "action=get",
            "axis-cgi/ntp.cgi" => body.Contains("\"getNTPInfo\"", StringComparison.Ordinal),
            "axis-cgi/applications/list.cgi" => body.Length == 0,
            "axis-cgi/applications/config.cgi" => uri.Contains("action=get", StringComparison.Ordinal) && body.Length == 0,
            "vapix/services" => body.Contains("GetWebServerTlsConfiguration", StringComparison.Ordinal) && !body.Contains("SetWebServer", StringComparison.Ordinal),
            _ => false,
        };
        if (!allowed)
        {
            lock (Violations)
            {
                Violations.Add($"{method} {uri} {body}");
            }
        }
    }

    private void Record(string line)
    {
        lock (Requests)
        {
            Requests.Add(line);
        }
    }
}

internal sealed class FakeVapixFactory : IVapixClientFactory
{
    public Dictionary<Guid, FakeCamera> Cameras { get; } = [];

    public int Created { get; private set; }

    public FakeCamera Add(Guid id, FakeCamera? camera = null) => Cameras[id] = camera ?? new FakeCamera();

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        Created++;
        return Cameras.TryGetValue(deviceId, out var camera)
            ? Task.FromResult<IVapixClient>(camera)
            : throw new KeyNotFoundException("no such device");
    }
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

internal sealed class MemorySettings : IPluginSettings
{
    public Dictionary<string, string?> Values { get; } = [];

    public int Writes { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        lock (Values)
        {
            return Task.FromResult(Values.GetValueOrDefault(key));
        }
    }

    public Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        lock (Values)
        {
            Values[key] = valueJson;
            Writes++;
        }

        return Task.CompletedTask;
    }
}

internal sealed class RecordingEvents : IPluginEvents
{
    public List<PluginEvent> Events { get; } = [];

    public void Publish(string topic, string? payloadJson)
    {
        lock (Events)
        {
            Events.Add(new PluginEvent(topic, payloadJson));
        }
    }

    public List<DeviceResult> Results
    {
        get
        {
            lock (Events)
            {
                return [.. Events.Where(e => e.Topic == HardeningMethods.ResultsTopic).SelectMany(e => HardeningJson.Deserialize<ResultsEvent>(e.PayloadJson).Results)];
            }
        }
    }

    public List<ScanJobStatus> Progress
    {
        get
        {
            lock (Events)
            {
                return [.. Events.Where(e => e.Topic == HardeningMethods.ProgressTopic).Select(e => HardeningJson.Deserialize<ScanJobStatus>(e.PayloadJson))];
            }
        }
    }
}

internal sealed class TestCoreContext(FakeDevices devices, FakeVapixFactory vapix, IPluginSettings settings, IPluginEvents? events) : ICorePluginContext
{
    public IDeviceRepository Devices { get; } = devices;

    public IVapixClientFactory Vapix { get; } = vapix;

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings { get; } = settings;

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events { get; } = events;
}

/// <summary>The page context against an in-process plugin, or a scripted backend.</summary>
internal sealed class PageContext : ICorePluginClientContext
{
    private readonly Func<string, string?, Task<string?>> _invoke;

    public PageContext(HardeningScanPlugin plugin)
        : this((method, payload) => plugin.InvokeAsync(method, payload, CancellationToken.None))
    {
    }

    public PageContext(Func<string, string?, Task<string?>> invoke)
    {
        _invoke = invoke;
    }

    public List<IDeviceInfo> DeviceList { get; } = [];

    public List<IDeviceInfo> Selection { get; } = [];

    public List<string> Calls { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public IReadOnlyList<IDeviceInfo> SelectedDevices => Selection;

    public event EventHandler? DevicesChanged;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(method);
        }

        return _invoke(method, payloadJson);
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
