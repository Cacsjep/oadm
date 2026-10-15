using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ActionRules.Tests;

internal sealed class FakeDevice : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Serial { get; init; } = "B8A44F914A20";
    public string Address { get; init; } = "10.0.0.202";
    public string? HostName { get; init; }
    public string? Model { get; init; } = "AXIS M3215-LVE";
    public string? FirmwareVersion { get; init; } = "12.11.77";
    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;
    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;
    public bool HasVideo => DeviceCategories.HasVideo(Category);
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];
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

/// <summary>One <see cref="RuleCamera"/> per device id (created on first use, AXIS OS 12 unless set).</summary>
internal sealed class FakeCameras : IVapixClientFactory
{
    public ConcurrentDictionary<Guid, RuleCamera> Cameras { get; } = new();

    /// <summary>A real camera for every device (hardware tests); the test disposes it.</summary>
    public IVapixClient? Fixed { get; set; }

    public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        Task.FromResult(Fixed ?? Cameras.GetOrAdd(deviceId, _ => new RuleCamera()));
}

/// <summary>Records the runs a core plugin starts.</summary>
internal sealed class RecordingRunner : ITaskRunner
{
    public List<(string PluginId, IReadOnlyList<Guid> DeviceIds, string? Payload, string Owner)> Runs { get; } = [];

    /// <summary>Tasks started and not cancelled (they never end by themselves here).</summary>
    public ConcurrentDictionary<Guid, bool> Active { get; } = new();

    public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        Runs.Add((pluginId, deviceIds, payloadJson, owner));
        IReadOnlyList<Guid> ids = [.. deviceIds.Select(_ => Guid.NewGuid())];
        foreach (var id in ids)
        {
            Active[id] = true;
        }

        return Task.FromResult(ids);
    }

    public bool Cancel(Guid taskId) => Active.TryRemove(taskId, out _);

    public bool? IsActive(Guid taskId) => Active.ContainsKey(taskId);
}

internal sealed class RecordingEvents : IPluginEvents
{
    public ConcurrentQueue<(string Topic, string? Payload)> Published { get; } = new();

    public void Publish(string topic, string? payloadJson) => Published.Enqueue((topic, payloadJson));
}

/// <summary>Reversible "encryption" that marks its output, to prove what is stored encrypted.</summary>
internal sealed class FakeSecrets : ISecretProtector
{
    public string Protect(string plaintext, string purpose) => "enc:" + purpose + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedValue, string purpose)
    {
        var prefix = "enc:" + purpose + ":";
        if (!protectedValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new System.Security.Cryptography.CryptographicException("Wrong purpose.");
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[prefix.Length..]));
    }
}

internal sealed class FakeContext(IDeviceRepository devices, FakeCameras cameras, RecordingRunner tasks, RecordingEvents events, string? dataDirectory = null) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => cameras;

    public ITaskRunner Tasks => tasks;

    public IPluginSettings Settings => throw new NotSupportedException();

    public ILogger Logger => NullLogger.Instance;

    /// <summary>The page's event hub instead of the recorder (page tests).</summary>
    public IPluginEvents? Hub { get; init; }

    public IPluginEvents? Events => Hub ?? events;

    public ISecretProtector? Secrets { get; } = new FakeSecrets();

    public string? DataDirectory => dataDirectory;
}
