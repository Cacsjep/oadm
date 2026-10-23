using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Answer of the export query: the device's export, or why there is none.</summary>
public sealed record ExportReply
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The device's <c>$export</c> answer, unchanged.</summary>
    public string? Text { get; init; }

    public string? Error { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ExportReply Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new ExportReply { Error = "The server sent no answer." } : JsonSerializer.Deserialize<ExportReply>(json, Options) ?? new ExportReply();
}

/// <summary>
/// "Export configuration" (Maintenance): a read-only dialog, never a task. The dialog reads every device through the query
/// <see cref="DeviceConfigIds.ExportQuery"/> (at most <see cref="DeviceConfigIds.ExportParallelism"/> at a time) and saves
/// the answers.
/// </summary>
public sealed class ExportConfigTask : ITaskPlugin, ITaskPluginQuery
{
    public string Id => DeviceConfigIds.Export;

    public string DisplayName => DeviceConfigIds.ExportName;

    public string? IconKey => "export";

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => DeviceConfigSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => DeviceConfigSupport.NotSupportedReason(device);

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        throw new InvalidOperationException("\"Export configuration\" only reads; it never runs as a task.");

    /// <summary>The user's query (SDK 0.4.0): reads only the queried device, which the host checked against the user's view.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, DeviceConfigIds.ExportQuery, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown query '{method}'.", nameof(method));
        }

        if (TaskSupportReasons.ForStatus(device) is { } refused)
        {
            return new ExportReply { Error = refused }.ToJson();
        }

        try
        {
            return new ExportReply { Text = await ConfigApi.ExportAsync(ctx.Vapix, ct).ConfigureAwait(false) }.ToJson();
        }
        catch (ConfigDeviceException ex)
        {
            return new ExportReply { Error = ex.Message }.ToJson();
        }
    }
}

/// <summary>Which devices export and import configuration files (cached data only).</summary>
public static class DeviceConfigSupport
{
    public static bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.Ok && FirmwareVersions.SupportsConfigFiles(device.FirmwareVersion);
    }

    public static string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device, requireOk: true)
            ?? TaskSupportReasons.NeedsFirmware(DeviceConfigIds.MinFirmwareText, device);
    }
}
