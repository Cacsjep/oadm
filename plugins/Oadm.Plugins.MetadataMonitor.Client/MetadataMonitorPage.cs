using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.MetadataMonitor.Client;

/// <summary>Navigation rail page of the "Metadata Monitor" core plugin: one card (the host's page card).</summary>
public sealed class MetadataMonitorPage : ICorePluginPage
{
    public string PluginId => MetadataMonitorPluginInfo.PluginId;

    public string Title => MetadataMonitorPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new MetadataMonitorView { DataContext = new MetadataMonitorViewModel(ctx) };
    }
}
