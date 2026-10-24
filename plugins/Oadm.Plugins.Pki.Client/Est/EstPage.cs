using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Est.Client;

/// <summary>The page of the "EST server" core plugin: its own cards (General and Profiles, then Enrollments). A tab of the PKI page (<see cref="TabOf"/>), no rail entry of its own.</summary>
public sealed class EstPage : ICorePluginPage
{
    public string PluginId => EstPluginInfo.PluginId;

    public string Title => EstPluginInfo.DisplayName;

    /// <summary>The enrollments are a card of their own, visibly apart from General and Profiles.</summary>
    public bool HasOwnCards => true;

    /// <summary>The page is the "EST server" tab of the PKI page (docs/spec/pki.md "The PKI page").</summary>
    public string? TabOf => Oadm.Plugins.Pki.PkiPluginInfo.PluginId;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new EstView { DataContext = new EstViewModel(ctx) };
    }
}
