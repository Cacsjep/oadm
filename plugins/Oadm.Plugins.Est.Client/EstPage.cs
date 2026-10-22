using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Est.Client;

/// <summary>Navigation rail page of the "EST server" core plugin: one card (in the host card).</summary>
public sealed class EstPage : ICorePluginPage
{
    public string PluginId => EstPluginInfo.PluginId;

    public string Title => EstPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new EstView { DataContext = new EstViewModel(ctx) };
    }
}
