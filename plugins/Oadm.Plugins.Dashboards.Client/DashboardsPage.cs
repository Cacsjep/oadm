using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>Navigation rail page of the "Dashboards" core plugin: its own cards (dashboards, cameras, events; the open dashboards).</summary>
public sealed class DashboardsPage : ICorePluginPage
{
    public string PluginId => DashboardsPluginInfo.PluginId;

    public string Title => DashboardsPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // The host disposes the view model (IDisposable) when the page is removed; leaving the page only hides it.
        return new DashboardsPageView { DataContext = new DashboardsPageViewModel(ctx) };
    }
}
