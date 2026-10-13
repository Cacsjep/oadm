using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.EdgeStorage.Client;

/// <summary>Navigation rail page of the "Edge storage" core plugin: count cards, the table card, the tasks pane below.</summary>
public sealed class EdgeStoragePage : ICorePluginPage
{
    public string PluginId => EdgeStorageInfo.PluginId;

    public string Title => EdgeStorageInfo.DisplayName;

    public bool HasOwnCards => true;

    /// <summary>The runs of the tools are tasks: their progress shows below the table.</summary>
    public bool ShowTasksPane => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new EdgeStorageView { DataContext = new EdgeStorageViewModel(ctx) };
    }
}
