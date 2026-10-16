using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Samples.FirmwareOverview.Tests;

internal sealed record FakeDevice(string Address, DeviceStatus Status = DeviceStatus.Ok) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Serial => "B8A44F631339";
    public string? HostName => null;
    public string? Model => "P3265-V";
    public string? FirmwareVersion => null;
    public DeviceCategory Category => DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<DeviceApi> Apis => [];
}

/// <summary>A camera that answers basicdeviceinfo with its AXIS OS version.</summary>
internal sealed class FakeCamera(string version) : IVapixClient
{
    public int Reads { get; private set; }

    public Uri BaseAddress { get; } = new("https://192.0.2.10/");

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(new BasicDeviceInfo("B8A44F631339", "P3265-V", null, null, version, null, null));
    }

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();
    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>The server's context for the core plugin: the devices, their cameras and the published events.</summary>
internal sealed class FakeCoreContext : ICorePluginContext, IDeviceRepository, IVapixClientFactory, IPluginEvents
{
    public List<IDeviceInfo> DeviceList { get; } = [];
    public Dictionary<Guid, FakeCamera> Cameras { get; } = [];
    public Channel<PluginEvent> Published { get; } = Channel.CreateUnbounded<PluginEvent>();

    public IDeviceRepository Devices => this;
    public IVapixClientFactory Vapix => this;
    public ITaskRunner Tasks => throw new NotSupportedException();
    public IPluginSettings Settings => throw new NotSupportedException();
    public ILogger Logger => NullLogger.Instance;
    public IPluginEvents Events => this;

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>(DeviceList);
    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(DeviceList.FirstOrDefault(d => d.Id == id));
    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<IVapixClient>(Cameras[deviceId]);
    public void Publish(string topic, string? payloadJson) => Published.Writer.TryWrite(new PluginEvent(topic, payloadJson));
}

/// <summary>The page's context: calls go straight to the server part, events come from its fake context.</summary>
internal sealed class FakePageContext(FirmwareOverviewPlugin plugin, FakeCoreContext server) : ICorePluginClientContext
{
    public IReadOnlyList<IDeviceInfo> Devices => server.DeviceList;

    /// <summary>The Devices page selection: the page's device choice starts with it.</summary>
    public IReadOnlyList<IDeviceInfo> SelectedDevices { get; set; } = [];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in server.Published.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }
}
