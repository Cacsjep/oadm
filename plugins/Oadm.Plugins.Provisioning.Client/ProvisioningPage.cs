using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>Navigation rail page of the Provisioning core plugin: its own cards (DHCP server, cameras, a camera's flow).</summary>
public sealed class ProvisioningPage : ICorePluginPage
{
    public string PluginId => ProvisioningInfo.PluginId;

    public string Title => ProvisioningInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new ProvisioningView { DataContext = new ProvisioningViewModel(ctx) };
    }
}
