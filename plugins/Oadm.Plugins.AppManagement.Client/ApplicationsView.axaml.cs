using System.ComponentModel;

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>
/// Shows the page; the view model connects while the view is in the visual tree. Hands both grids' selections over in one
/// step and selects the kept devices again after a rebuild (layout only).
/// </summary>
public partial class ApplicationsView : UserControl, IApplicationDialogs
{
    private ApplicationsViewModel? _viewModel;
    private bool _reselecting;

    /// <summary>The smallest share of the page width each card keeps when the splitter moves.</summary>
    public const double MinPaneShare = 0.2;

    public ApplicationsView()
    {
        InitializeComponent();
        DevicesGrid.SelectionChanged += (_, _) =>
        {
            if (!_reselecting)
            {
                _viewModel?.SetSelectedDevices(DevicesGrid.SelectedItems.OfType<AppDeviceRow>());
            }
        };
        // Each card keeps at least a fifth of the width; the splitter moves the line between them.
        Panes.SizeChanged += (_, e) =>
        {
            var min = e.NewSize.Width * MinPaneShare;
            Panes.ColumnDefinitions[0].MinWidth = min;
            Panes.ColumnDefinitions[2].MinWidth = min;
        };
        AppsGrid.SelectionChanged += (_, _) =>
        {
            if (!_reselecting)
            {
                _viewModel?.SetDeviceSelection(AppsGrid.SelectedItems.OfType<AppRow>());
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.DevicesRebuilt -= OnDevicesRebuilt;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ApplicationsViewModel;
        ApplyAppsSource();
        if (_viewModel is { } vm)
        {
            vm.DevicesRebuilt += OnDevicesRebuilt;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.Dialogs = this;
            vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner
                && await vm.DeviceChoice.PickAsync(owner, "Choose devices").ConfigureAwait(true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationsViewModel.GroupApps))
        {
            ApplyAppsSource();
        }
    }

    /// <summary>The lower grid's rows, grouped by device (header: the address, no Address column) while several devices are selected.</summary>
    private void ApplyAppsSource()
    {
        if (_viewModel is not { } vm)
        {
            AppsGrid.ItemsSource = null;
            return;
        }

        foreach (var column in AppsGrid.Columns.Where(c => c.Tag is "address"))
        {
            column.IsVisible = !vm.GroupApps;
        }
        if (!vm.GroupApps)
        {
            AppsGrid.Classes.Remove("groups");
            AppsGrid.ItemsSource = vm.SelectedApps;
            return;
        }

        var view = new DataGridCollectionView(vm.SelectedApps);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(AppRow.Group)));
        AppsGrid.Classes.Add("groups");
        AppsGrid.ItemsSource = view;
    }

    /// <summary>After a rebuild both grids select the kept devices and application rows again (one pass over the rows).</summary>
    private void OnDevicesRebuilt(object? sender, EventArgs e)
    {
        if (_viewModel is not { } vm || vm.SelectedDeviceIds.Count == 0)
        {
            return;
        }

        _reselecting = true;
        try
        {
            DevicesGrid.SelectedItems.Clear();
            foreach (var row in vm.Devices.Where(d => vm.SelectedDeviceIds.Contains(d.DeviceId)))
            {
                DevicesGrid.SelectedItems.Add(row);
            }

            AppsGrid.SelectedItems.Clear();
            foreach (var row in vm.SelectedRows)
            {
                AppsGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _reselecting = false;
        }
    }

    public async Task ShowLogAsync(AppLogViewModel log)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new AppLogWindow { DataContext = log }.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    public async Task ShowSettingsAsync(AppSettingsViewModel settings)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new AppSettingsWindow { DataContext = settings }.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    public async Task<bool> ShowInstallAsync(InstallAppsViewModel install) =>
        TopLevel.GetTopLevel(this) is Window owner
        && await new InstallAppsWindow { DataContext = install }.ShowDialog<bool>(owner).ConfigureAwait(true);

    public async Task<bool> ShowLicensesAsync(InstallLicensesViewModel licenses) =>
        TopLevel.GetTopLevel(this) is Window owner
        && await new InstallLicensesWindow { DataContext = licenses }.ShowDialog<bool>(owner).ConfigureAwait(true);

    /// <summary>
    /// An application row: Settings (of the selected rows when the row is one of them) when the application has settings,
    /// else Open app page when it has its own page (greyed out while Web View is off); Show log always. The button waits while
    /// the device is asked once.
    /// </summary>
    private async void OnRowActions(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppRow row } button || DataContext is not ApplicationsViewModel vm)
        {
            return;
        }

        button.IsEnabled = false;
        AppRowMenu menu;
        try
        {
            menu = await vm.MenuForAsync(row, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            button.IsEnabled = true;
        }

        List<IssuedCertificateAction> items = [];
        if (menu.Settings)
        {
            items.Add(new("Settings", vm.ShowSettingsCommand, row));
        }
        else if (menu.AppPage || menu.WebViewOff)
        {
            items.Add(new(OpenAppPageText, vm.OpenAppPageCommand, row));
        }

        items.Add(new("Show log", vm.ShowLogCommand, row));
        if (!menu.WebViewOff)
        {
            RowActions.Show(button, items);
            return;
        }

        // Web View off: Open app page greyed out, its tooltip says what to do (the look of RowActions otherwise).
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var item in IssuedCertificateList.MenuItems(items))
        {
            if (item is MenuItem { Header: OpenAppPageText } page)
            {
                page.IsEnabled = false;
                ToolTip.SetTip(page, AppSettingsViewModel.WebViewOffTip);
                ToolTip.SetShowOnDisabled(page, true);
            }

            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private const string OpenAppPageText = "Open app page";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ApplicationsViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as ApplicationsViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
