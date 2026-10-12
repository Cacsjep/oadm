using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>Navigation rail page of the "Edge Recording Export" core plugin: one card of its own.</summary>
public sealed class EdgeRecordingPage : ICorePluginPage
{
    public string PluginId => EdgeRecordingInfo.PluginId;

    public string Title => EdgeRecordingInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // The host disposes the view model (IDisposable) when the page is removed; leaving the page only hides it, so a
        // download goes on.
        return new EdgeRecordingView { DataContext = new EdgeRecordingViewModel(ctx) };
    }
}
