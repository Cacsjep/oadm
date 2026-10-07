using Avalonia.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client;

/// <summary>Client part of a task plugin that needs user input before running.</summary>
public interface ITaskPluginDialog
{
    string PluginId { get; }
    /// <summary>Returns the payload JSON sent to the server, or null when the user cancelled.</summary>
    Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner);
}

/// <summary>Server access for a task plugin dialog. Build dialogs from the shared OADM controls and theme.</summary>
public interface ITaskDialogContext
{
    /// <summary>Calls the plugin's server-side <c>ITaskPluginQuery.QueryAsync</c> for one device (read-only).</summary>
    Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// Uploads a local file to the server (256 KB chunks); put the returned id into the task payload.
    /// <paramref name="progress"/> receives the fraction sent, from 0.0 to 1.0 (not a percentage).
    /// </summary>
    Task<Oadm.Sdk.Plugins.UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>UI page of a core plugin, shown in the navigation rail.</summary>
public interface ICorePluginPage
{
    string PluginId { get; }
    string Title { get; }
    Control CreateView(ICorePluginClientContext ctx);
}

public interface ICorePluginClientContext
{
    /// <summary>Calls ICorePlugin.InvokeAsync on the server.</summary>
    Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct);
}
