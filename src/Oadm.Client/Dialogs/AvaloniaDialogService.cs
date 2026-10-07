using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using Oadm.Client.Discovery;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Dialogs;

public sealed class AvaloniaDialogService : IDialogService, IUrlLauncher
{
    public async Task ShowMessageAsync(string title, string message)
    {
        if (Owner() is not { } owner)
        {
            return;
        }

        var window = new MessageWindow { DataContext = new MessageDialogModel(title, message, "OK", "", false) };
        await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (Owner() is not { } owner)
        {
            return false;
        }

        var window = new MessageWindow { DataContext = new MessageDialogModel(title, message, confirmText, "Cancel", true) };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    public async Task<CommitReply?> ShowAddDevicesWizardAsync(AddDevicesWizardViewModel wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);
        if (Owner() is not { } owner)
        {
            return null;
        }

        var window = new AddDevicesWizardWindow { DataContext = wizard };
        bool added = await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        return added ? wizard.Result : null;
    }

    public async Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (Owner() is not { } owner)
        {
            return null;
        }

        return await dialog.ShowAsync(devices, owner).ConfigureAwait(true);
    }

    public async Task ShowTaskDetailsAsync(TaskDetailsViewModel details)
    {
        if (Owner() is not { } owner)
        {
            return;
        }

        var window = new TaskDetailsWindow { DataContext = details };
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public async Task<bool> OpenAsync(Uri uri)
    {
        if (Owner() is not { } owner)
        {
            return false;
        }

        return await owner.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
    }

    /// <summary>The active window (so dialogs opened from dialogs stack correctly), else the main window.</summary>
    private static Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }

        return null;
    }
}
