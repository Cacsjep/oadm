using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Users.Client;

/// <summary>Client part of the "Users..." task: opens <see cref="UsersWindow"/> and returns the payload.</summary>
public sealed class UsersTaskDialog : ITaskPluginDialog
{
    public string PluginId => UsersTaskPlugin.PluginId;

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(owner);

        var vm = new UsersDialogViewModel(devices);
        using var loading = new CancellationTokenSource();
        var window = new UsersWindow { DataContext = vm };
        var load = vm.LoadAsync(ctx, loading.Token);
        try
        {
            return await window.ShowDialog<string?>(owner).ConfigureAwait(true);
        }
        finally
        {
            await loading.CancelAsync().ConfigureAwait(true);
            await load.ConfigureAwait(true);
        }
    }
}
