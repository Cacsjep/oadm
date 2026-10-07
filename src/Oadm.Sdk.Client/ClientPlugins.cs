using Avalonia.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client;

/// <summary>Client part of a task plugin that needs user input before running.</summary>
public interface ITaskPluginDialog
{
    string PluginId { get; }
    /// <summary>Returns the payload JSON sent to the server, or null when the user cancelled.</summary>
    Task<string?> ShowAsync(IReadOnlyList<IDeviceInfo> devices, Window owner);
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
