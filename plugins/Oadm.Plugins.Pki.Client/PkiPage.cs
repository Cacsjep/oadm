using Avalonia.Controls;

using Oadm.Plugins.Pki.TrustStore;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.Pki.Client;

/// <summary>Navigation rail page of the "PKI" core plugin: several cards (certificate authority, device certificates, 802.1X, previous CAs).</summary>
public sealed class PkiPage : ICorePluginPage
{
    public string PluginId => PkiPluginInfo.PluginId;

    public string Title => PkiPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new PkiView { DataContext = new PkiViewModel(ctx, TrustStoreInstallers.ForClient()) };
    }
}
