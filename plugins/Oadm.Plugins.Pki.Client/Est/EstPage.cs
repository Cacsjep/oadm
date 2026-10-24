using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Est.Client;

/// <summary>Navigation rail page of the "EST server" core plugin: its own cards (General and Profiles, then Enrollments).</summary>
public sealed class EstPage : ICorePluginPage
{
    public string PluginId => EstPluginInfo.PluginId;

    public string Title => EstPluginInfo.DisplayName;

    /// <summary>The enrollments are a card of their own, visibly apart from General and Profiles.</summary>
    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new EstView { DataContext = new EstViewModel(ctx) };
    }
}
