using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>Navigation rail page of the "Live View" core plugin: its own cards (views and cameras, the open views).</summary>
public sealed class LiveViewPage : ICorePluginPage
{
    public string PluginId => LiveViewPluginInfo.PluginId;

    public string Title => LiveViewPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // The host disposes the view model (IDisposable) when the page is removed; leaving the page only hides it.
        return new LiveViewPageView { DataContext = new LiveViewPageViewModel(ctx) };
    }
}
