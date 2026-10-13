using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Samples.UpnpName.Tests;

/// <summary>A camera that knows the param.cgi list and update calls.</summary>
internal sealed class FakeCamera : IVapixClient
{
    public Dictionary<string, string> Parameters { get; } = new() { [UpnpName.Parameter] = "AXIS P3265-V - B8A44F631339" };
    public IReadOnlyList<DeviceApi> Apis { get; set; } = [new("param-cgi", "1.0")];
    public int Writes { get; private set; }

    public Uri BaseAddress { get; } = new("https://192.0.2.10/");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult(Apis);

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Parameters.Where(p => groups.Any(g => p.Key.StartsWith(g, StringComparison.Ordinal))).ToDictionary());

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        foreach (var pair in body.Split('&').Skip(1)) // after "action=update"
        {
            var parts = pair.Split('=', 2);
            Parameters[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }

        Writes++;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();
}

internal sealed record FakeDevice(DeviceStatus Status = DeviceStatus.Ok, IReadOnlyList<DeviceApi>? ApiList = null) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Serial => "B8A44F631339";
    public string Address => "192.0.2.10";
    public string? HostName => null;
    public string? Model => "P3265-V";
    public string? FirmwareVersion => "12.11.77";
    public DeviceCategory Category => DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<DeviceApi> Apis => ApiList ?? [new("param-cgi", "1.0")];
}

/// <summary>The task context of the server, recording steps and warnings.</summary>
internal sealed class FakeTaskContext(IVapixClient vapix) : ITaskExecutionContext
{
    public TaskStepList Steps { get; } = new();
    public List<string> Warnings { get; } = [];

    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix => vapix;
    public ILogger Logger => NullLogger.Instance;
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files => throw new NotSupportedException();

    public void PlanSteps(params string[] names) => Steps.Plan(names);
    public ITaskStep BeginStep(string name) => Steps.Begin(name);
    public void ReportProgress(int percent, string? message = null) { }
    public void ReportWarning(string message) => Warnings.Add(message);
    public void Log(TaskLogLevel level, string message) { }
}
