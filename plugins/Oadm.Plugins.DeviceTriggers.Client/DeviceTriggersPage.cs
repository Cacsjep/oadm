using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.DeviceTriggers.Client;

/// <summary>Navigation rail page of the "Device triggers" core plugin (own cards: triggers, devices of the selected one).</summary>
public sealed class DeviceTriggersPage : ICorePluginPage
{
    public string PluginId => DeviceTriggersPluginInfo.PluginId;

    public string Title => DeviceTriggersPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new DeviceTriggersView { DataContext = new DeviceTriggersViewModel(ctx) };
    }
}
