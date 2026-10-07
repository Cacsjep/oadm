using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>Client part of the VAPIX Commander core plugin: the page in the navigation rail.</summary>
public sealed class VapixCommanderPage : ICorePluginPage
{
    public string PluginId => VapixCommanderPlugin.PluginId;

    public string Title => "VAPIX Commander";

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var vm = new CommanderViewModel(new CommanderBackend(ctx), ctx);
        _ = vm.LoadAsync(CancellationToken.None);
        return new CommanderView { DataContext = vm };
    }
}
