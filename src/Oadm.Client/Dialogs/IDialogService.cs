using Oadm.Client.Discovery;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Dialogs;

/// <summary>Modal UI used by view models. The Avalonia implementation lives in <see cref="AvaloniaDialogService"/>.</summary>
public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Shows the add devices wizard; returns the commit reply, or null when cancelled.</summary>
    Task<CommitReply?> ShowAddDevicesWizardAsync(AddDevicesWizardViewModel wizard);

    /// <summary>Shows a client plugin dialog; returns the payload JSON, or null when cancelled.</summary>
    Task<string?> ShowTaskPluginDialogAsync(ITaskPluginDialog dialog, IReadOnlyList<IDeviceInfo> devices);

    Task ShowTaskDetailsAsync(TaskDetailsViewModel details);
}

/// <summary>Opens URLs (device web UI) in the default browser.</summary>
public interface IUrlLauncher
{
    Task<bool> OpenAsync(Uri uri);
}
