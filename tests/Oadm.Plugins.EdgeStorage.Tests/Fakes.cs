using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage.Tests;

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

    public IReadOnlyList<DeviceApi> Apis { get; init; } =
        [new("disk-management", "1.0", "Disk management", "official"), new("disk-properties", "1.1", "Disk properties", "official")];
}

/// <summary>A task context that records the steps (the engine's <see cref="TaskStepList"/>) and warnings.</summary>
internal sealed class RecordingContext : ITaskExecutionContext
{
    public RecordingContext(IVapixClient vapix)
    {
        Vapix = vapix;
        Steps = new TaskStepList(onWarning: ReportWarning);
    }

    public TaskStepList Steps { get; }
    public List<string> Warnings { get; } = [];
    public List<(TaskLogLevel Level, string Message)> Logs { get; } = [];
    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix { get; }
    public ILogger Logger => NullLogger.Instance;
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files => throw new NotSupportedException("No uploads in this plugin.");

    public void ReportProgress(int percent, string? message = null)
    {
    }

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => Logs.Add((level, message));
}

internal sealed class FakeDevices : IDeviceRepository
{
    public List<IDeviceInfo> Items { get; } = [];

    public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Items]);

    public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
}

/// <summary>One <see cref="SdCamera"/> per device id (created on first use).</summary>
internal sealed class FakeCameras : IVapixClientFactory
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, SdCamera> Cameras { get; } = new();

    public int Created;

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
    {
        Interlocked.Increment(ref Created);
        return Task.FromResult<IVapixClient>(Cameras.GetOrAdd(deviceId, _ => new SdCamera()));
    }
}

/// <summary>Records the runs a core plugin starts.</summary>
internal sealed class RecordingRunner : ITaskRunner
{
    public List<(string PluginId, IReadOnlyList<Guid> DeviceIds, string? Payload, string Owner)> Runs { get; } = [];

    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        Runs.Add((pluginId, deviceIds, payloadJson, owner));
        return Task.FromResult<IReadOnlyList<Guid>>([.. deviceIds.Select(_ => Guid.NewGuid())]);
    }
}

internal sealed class RecordingEvents : IPluginEvents
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Topic, string? Payload)> Published { get; } = new();

    public void Publish(string topic, string? payloadJson) => Published.Enqueue((topic, payloadJson));
}

internal sealed class FakeContext(IDeviceRepository devices, FakeCameras cameras, RecordingRunner tasks, RecordingEvents events) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => cameras;

    public ITaskRunner Tasks => tasks;

    public IPluginSettings Settings => throw new NotSupportedException();

    public ILogger Logger => NullLogger.Instance;

    /// <summary>The page's event hub instead of the recorder (page tests).</summary>
    public IPluginEvents? Hub { get; init; }

    public IPluginEvents? Events => Hub ?? events;
}
