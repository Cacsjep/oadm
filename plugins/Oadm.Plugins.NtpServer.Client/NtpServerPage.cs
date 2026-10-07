using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.NtpServer.Client;

/// <summary>Navigation rail page of the "NTP server" core plugin: one card (in the host card).</summary>
public sealed class NtpServerPage : ICorePluginPage
{
    public string PluginId => NtpServerPluginInfo.PluginId;

    public string Title => NtpServerPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new NtpServerView { DataContext = new NtpServerViewModel(ctx) };
    }
}
