using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>Navigation rail page of the "Schedules" core plugin (own cards: the schedules, the runs of the selected one).</summary>
public sealed class SchedulesPage : ICorePluginPage
{
    public string PluginId => SchedulesInfo.PluginId;

    public string Title => SchedulesInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new SchedulesView { DataContext = new SchedulesViewModel(ctx) };
    }
}
