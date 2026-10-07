using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>Navigation rail page of the "DHCP server" core plugin: one card (in the host card).</summary>
public sealed class DhcpServerPage : ICorePluginPage
{
    public string PluginId => DhcpServerPluginInfo.PluginId;

    public string Title => DhcpServerPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new DhcpServerView { DataContext = new DhcpServerViewModel(ctx) };
    }
}
