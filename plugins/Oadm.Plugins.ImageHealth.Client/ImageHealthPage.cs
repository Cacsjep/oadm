using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>Navigation rail page of the "Image Health Dashboard" core plugin: a row of count cards, then the table card.</summary>
public sealed class ImageHealthPage : ICorePluginPage
{
    public string PluginId => ImageHealthPluginInfo.PluginId;

    public string Title => ImageHealthPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new ImageHealthView { DataContext = new ImageHealthViewModel(ctx) };
    }
}
