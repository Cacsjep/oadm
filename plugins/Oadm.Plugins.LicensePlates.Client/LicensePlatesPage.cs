using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>Rail page "License Plate Center" (own cards: the tabs Log, Plates, Cameras, Stats).</summary>
public sealed class LicensePlatesPage : ICorePluginPage
{
    public string PluginId => LicensePlatesInfo.PluginId;

    public string Title => LicensePlatesInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new LicensePlatesView { DataContext = new LicensePlatesViewModel(ctx) };
    }
}
