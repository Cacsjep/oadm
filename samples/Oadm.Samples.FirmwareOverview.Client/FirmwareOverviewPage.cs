using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Samples.FirmwareOverview.Client;

/// <summary>Found by the client loader: the rail entry and its page.</summary>
public sealed class FirmwareOverviewPage : ICorePluginPage
{
    public string PluginId => FirmwareOverview.PluginId;

    public string Title => "Firmware overview";

    // Called for every view of the page (the rail page and each "Open in new window"), each with its own context.
    public Control CreateView(ICorePluginClientContext ctx) => new FirmwareOverviewView { DataContext = new FirmwareOverviewViewModel(ctx) };
}
