using Microsoft.Extensions.Logging;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Plugins;

public interface IPlugin
{
    /// <summary>Stable id, e.g. "oadm.restart".</summary>
    string Id { get; }
    string DisplayName { get; }
    string? IconKey { get; }
}

public interface ITaskPlugin : IPlugin
{
    bool ShowInToolbar { get; }
    /// <summary>When true the client opens the matching ITaskPluginDialog before running.</summary>
    bool RequiresDialog { get; }
    /// <summary>
    /// Synchronous filter for the context menu. Plugins that change the device MUST check the
    /// required API versions here via <c>device.Apis.Supports(...)</c> (cached from the last full
    /// refresh) and again in <see cref="ExecuteAsync"/> against a fresh
    /// <c>ctx.Vapix.GetApiListAsync</c> with <c>Require(...)</c> before the first write.
    /// </summary>
    bool CanRun(IDeviceInfo device);

    /// <summary>
    /// Runs once per device. Return normally for Done, call <see cref="ITaskExecutionContext.ReportWarning"/>
    /// for Done with warnings, throw for Failed. <paramref name="payloadJson"/> comes from the dialog;
    /// it is kept in memory only and never persisted, so it may carry secrets such as passwords.
    /// </summary>
    Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);
}

public interface ICorePlugin : IPlugin
{
    /// <summary>Task plugins contributed by this core plugin, e.g. PKI contributes "Deploy certificate".</summary>
    IReadOnlyList<ITaskPlugin> TaskPlugins { get; }
    Task StartAsync(ICorePluginContext ctx, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    /// <summary>Backend for the plugin's UI page.</summary>
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct);
}

public interface ITaskExecutionContext
{
    Guid TaskId { get; }
    /// <summary>Pre-authenticated for the current device.</summary>
    IVapixClient Vapix { get; }
    ILogger Logger { get; }
    /// <summary>Set when the task was contributed by a core plugin.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>Files uploaded by the dialog (firmware, ACAP packages), referenced by id in the payload.</summary>
    IUploadedFiles Files { get; }

    /// <summary>Progress of the current device; the message is shown live and persisted as the last step.</summary>
    void ReportProgress(int percent, string? message = null);

    /// <summary>The device finishes as "Done with warnings" (unless it fails); the message is logged.</summary>
    void ReportWarning(string message);

    /// <summary>Entry in this task's log, per device, shown in the task details and persisted. Never log secrets.</summary>
    void Log(TaskLogLevel level, string message);
}

public enum TaskLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public interface ICorePluginContext
{
    IDeviceRepository Devices { get; }
    IVapixClientFactory Vapix { get; }
    ITaskRunner Tasks { get; }
    IPluginSettings Settings { get; }
    ILogger Logger { get; }
}

/// <summary>Settings store namespaced per plugin.</summary>
public interface IPluginSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string? valueJson, CancellationToken ct);
}

/// <summary>Content of plugin.json next to the plugin assemblies.</summary>
public sealed record PluginManifest(string Id, string Version, string MinSdkVersion, string? DisplayName = null);

public static class SdkInfo
{
    public const string Version = "0.1.0";
}
