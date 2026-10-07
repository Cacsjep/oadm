using System.Collections.Concurrent;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Tasks;

internal sealed record FakeDevice(Guid Id, DeviceStatus Status = DeviceStatus.Ok, DeviceCategory Category = DeviceCategory.Camera) : IDeviceInfo
{
    public string Serial => Id.ToString("N")[..12].ToUpperInvariant();

    public string Address => "192.0.2.1";

    public string? HostName => null;

    public string? Model => "M3106";

    public string? FirmwareVersion => "11.0.0";

    public bool HasVideo => DeviceCategories.HasVideo(Category);
}

internal sealed class FakeDeviceRepository : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, IDeviceInfo> _devices = new();

    public Guid Add(DeviceStatus status = DeviceStatus.Ok)
    {
        var device = new FakeDevice(Guid.NewGuid(), status);
        _devices[device.Id] = device;
        return device.Id;
    }

    public Guid[] AddMany(int count) => [.. Enumerable.Range(0, count).Select(_ => Add())];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. _devices.Values]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_devices.TryGetValue(id, out var d) ? d : null);
}

/// <summary>Scriptable VAPIX client: <see cref="Answers"/> decides per GetBasicDeviceInfo call whether the device answers.</summary>
internal sealed class FakeVapixClient : IVapixClient
{
    private int _pings;

    public Uri BaseAddress { get; } = new("http://192.0.2.1/");

    public int RestartCalls { get; private set; }

    public int Pings => _pings;

    /// <summary>Called with the 1-based ping number; true = device answers.</summary>
    public Func<int, bool> Answers { get; set; } = _ => true;

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _pings);
        if (!Answers(n))
        {
            throw new HttpRequestException("Connection refused");
        }

        return Task.FromResult(new BasicDeviceInfo("ACCC8E000001", "M3106", "M3106", null, "11.0.0", null, null));
    }

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task RestartAsync(CancellationToken ct)
    {
        RestartCalls++;
        return Task.CompletedTask;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeVapixClientFactory : IVapixClientFactory
{
    public HashSet<Guid> Failing { get; } = [];

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        if (Failing.Contains(deviceId))
        {
            throw new InvalidOperationException("No credentials stored for device.");
        }

        return Task.FromResult<IVapixClient>(new FakeVapixClient());
    }
}

/// <summary>Task plugin whose behavior is a delegate.</summary>
internal sealed class DelegateTaskPlugin(
    string id,
    Func<ITaskExecutionContext, IDeviceInfo, CancellationToken, Task> execute,
    Func<IDeviceInfo, bool>? canRun = null) : ITaskPlugin
{
    public string Id => id;

    public string DisplayName => "Test " + id;

    public string? IconKey => null;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool CanRun(IDeviceInfo device) => canRun?.Invoke(device) ?? true;

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        execute(ctx, device, ct);
}
