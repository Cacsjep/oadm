using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>Navigation rail page of the "Image Health Dashboard" core plugin: one card (the host's page card).</summary>
public sealed class ImageHealthPage : ICorePluginPage
{
    public string PluginId => ImageHealthPluginInfo.PluginId;

    public string Title => ImageHealthPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new ImageHealthView { DataContext = new ImageHealthViewModel(ctx) };
    }
}
