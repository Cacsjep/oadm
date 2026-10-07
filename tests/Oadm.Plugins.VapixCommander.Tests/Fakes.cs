using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

internal static class Samples
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Samples");

    public static CommandLibrary Library() => CommandLibrary.Load(Directory);

    public static CommandDefinition Brand => Library().Find("common.brand.read")!.Clone();

    public static CommandDefinition BasicDeviceInfo => Library().Find("common.basicdeviceinfo.read")!.Clone();

    public static CommandDefinition ShiftLevel => Library().Find("common.daynight.shiftlevel")!.Clone();

    public static IReadOnlyList<DeviceApi> P3265Apis { get; } =
    [
        new("param-cgi", "1.0"), new("basic-device-info", "1.3"), new("api-discovery", "1.1"), new("network-settings", "1.37"),
        new("user-management", "1.2"), new("daynight", "1.2"),
    ];

    public static Dictionary<string, JsonElement> Values(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => JsonSerializer.SerializeToElement(v.Value), StringComparer.Ordinal);

    public static CommandDefinition Parse(string json) => JsonSerializer.Deserialize<CommandDefinition>(json, CommandJson.Strict)!;
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
    public IReadOnlyList<DeviceApi> Apis { get; init; } = Samples.P3265Apis;
}

/// <summary>A request as the device saw it (the body is read at send time).</summary>
internal sealed record SeenRequest(string Method, string Uri, string? ContentType, string Body, IReadOnlyDictionary<string, string> Headers, TimeSpan? Timeout);

/// <summary>Fake device: every request goes to <see cref="Handler"/>; throw from it to simulate transport errors.</summary>
internal sealed class FakeVapix : IVapixClient, IDisposable
{
    public Uri BaseAddress { get; } = new("http://10.0.0.48/");

    public Func<SeenRequest, HttpResponseMessage> Handler { get; set; } = _ => Text("OK");

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = Samples.P3265Apis;

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    public int ApiListCalls;

    /// <summary>Delay before answering (to keep tasks running in engine tests).</summary>
    public TimeSpan Delay { get; set; }

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => Text(body, status, "application/json");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ApiListCalls);
        return Task.FromResult(ApiList);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new SeenRequest(
            request.Method.Method,
            request.RequestUri!.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body,
            headers,
            request.Options.TryGetValue(VapixRequestOptions.Timeout, out var timeout) ? timeout : null);
        Requests.Enqueue(seen);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return Handler(seen);
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    /// <summary>The factory owns clients; plugins must never dispose them.</summary>
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        GC.SuppressFinalize(this);
    }
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> All { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. All]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(All.FirstOrDefault(d => d.Id == id));
}

/// <summary>One <see cref="FakeVapix"/> per device id (created on demand).</summary>
internal sealed class FakeVapixFactory : IVapixClientFactory
{
    public ConcurrentDictionary<Guid, FakeVapix> Clients { get; } = new();

    public FakeVapix For(Guid deviceId) => Clients.GetOrAdd(deviceId, _ => new FakeVapix());

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(For(deviceId));
}

internal sealed class RecordingRunner : ITaskRunner
{
    public List<(string PluginId, IReadOnlyList<Guid> Devices, string? Payload, string Owner)> Runs { get; } = [];

    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        Runs.Add((pluginId, deviceIds, payloadJson, owner));
        return Task.FromResult<IReadOnlyList<Guid>>([.. deviceIds.Select(_ => Guid.NewGuid())]);
    }
}

internal sealed class FakeCoreContext : ICorePluginContext
{
    private static readonly InMemoryPluginSettingsProvider SharedSettings = new();

    public FakeCoreContext(ITaskRunner? tasks = null, bool withSecrets = true, IPluginSettings? settings = null)
    {
        Tasks = tasks ?? new RecordingRunner();
        Settings = settings ?? new InMemoryPluginSettingsProvider().GetSettings(VapixCommanderPlugin.PluginId);
        Secrets = withSecrets ? TestSecrets.Create() : null;
    }

    public FakeDevices DeviceList { get; } = new();

    public FakeVapixFactory VapixFactory { get; } = new();

    public IDeviceRepository Devices => DeviceList;

    public IVapixClientFactory Vapix => VapixFactory;

    public ITaskRunner Tasks { get; }

    public IPluginSettings Settings { get; }

    public ILogger Logger => NullLogger.Instance;

    public string? PluginDirectory => null;

    public ISecretProtector? Secrets { get; }

    public static IPluginSettings Shared => SharedSettings.GetSettings(VapixCommanderPlugin.PluginId);
}

internal static class TestSecrets
{
    public static ISecretProtector Create() => new PluginSecretProtector(new CredentialProtector(RandomNumberGenerator.GetBytes(CredentialProtector.KeySize)));
}
