using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>Navigation rail page of the "Hardening scan" core plugin: one card (the host's page card).</summary>
public sealed class HardeningScanPage : ICorePluginPage
{
    public string PluginId => HardeningScanPluginInfo.PluginId;

    public string Title => HardeningScanPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new HardeningScanView { DataContext = new HardeningScanViewModel(ctx) };
    }
}
