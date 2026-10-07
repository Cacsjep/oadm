using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Navigation rail page of the "Snapshot report" core plugin.</summary>
public sealed class SnapshotReportPage : ICorePluginPage
{
    public string PluginId => SnapshotReportPluginInfo.PluginId;

    public string Title => SnapshotReportPluginInfo.DisplayName;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var view = new SnapshotReportView();
        view.DataContext = new SnapshotReportViewModel(ctx, view);
        return view;
    }
}
