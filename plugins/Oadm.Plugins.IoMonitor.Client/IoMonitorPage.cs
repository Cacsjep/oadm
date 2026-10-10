using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.IoMonitor.Client;

/// <summary>Navigation rail page of the "I/O Monitor" core plugin: one card (the host's page card).</summary>
public sealed class IoMonitorPage : ICorePluginPage
{
    public string PluginId => IoMonitorPluginInfo.PluginId;

    public string Title => IoMonitorPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new IoMonitorView { DataContext = new IoMonitorViewModel(ctx) };
    }
}
