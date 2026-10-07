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
    bool CanRun(IDeviceInfo device);
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
    void ReportProgress(int percent, string? message = null);
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
