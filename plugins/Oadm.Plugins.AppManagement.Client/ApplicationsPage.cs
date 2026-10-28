using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>Rail page "Applications": one card with the chosen devices and the applications of the selected ones; the tasks pane below.</summary>
public sealed class ApplicationsPage : ICorePluginPage
{
    public string PluginId => AppManagementInfo.PluginId;

    public string Title => AppManagementInfo.DisplayName;

    public bool HasOwnCards => true;

    /// <summary>Start, Stop, Remove and the dialog's installs run as tasks: their progress shows below the page.</summary>
    public bool ShowTasksPane => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new ApplicationsView { DataContext = new ApplicationsViewModel(ctx) };
    }
}
