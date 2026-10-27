using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>Rail page "Load test": the toolbar card, the stat cards, the graphs and the camera table.</summary>
public sealed class LoadTestPage : ICorePluginPage
{
    public string PluginId => LoadTestInfo.PluginId;

    public string Title => LoadTestInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new LoadTestView { DataContext = new LoadTestViewModel(ctx) };
    }
}
