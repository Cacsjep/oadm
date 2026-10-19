using System.Collections.Specialized;

using Avalonia.Collections;
using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>
/// The Plugins page. Code-behind only builds the grouped grid view: one group per rail group of the packages, in rail
/// order (<see cref="PluginsViewModel.Groups"/>), like the device grid's group mode.
/// </summary>
public partial class PluginsPageView : UserControl
{
    private PluginsViewModel? _vm;

    public PluginsPageView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Plugins.CollectionChanged -= OnPluginsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as PluginsViewModel;
        if (_vm is not null)
        {
            _vm.Plugins.CollectionChanged += OnPluginsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
        }

        Regroup();
        base.OnDataContextChanged(e);
    }

    private void OnPluginsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Regroup();

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginsViewModel.HasSites))
        {
            ShowSitesColumn();
        }
    }

    /// <summary>Sites: only while the server has sites (a grid column is not in the tree: no binding).</summary>
    private void ShowSitesColumn()
    {
        if (PluginGrid.Columns.FirstOrDefault(c => Equals(c.Header, "Sites")) is { } sites)
        {
            sites.IsVisible = _vm?.HasSites == true;
        }
    }

    private void Regroup()
    {
        if (_vm is null)
        {
            PluginGrid.ItemsSource = null;
            return;
        }

        var view = new DataGridCollectionView(_vm.Plugins);
        var byGroup = new DataGridPathGroupDescription(nameof(PluginRowViewModel.Group));
        foreach (var key in _vm.Groups)
        {
            byGroup.GroupKeys.Add(key);
        }

        view.GroupDescriptions.Add(byGroup);
        PluginGrid.ItemsSource = view;
        PluginGrid.Columns[^1].IsVisible = _vm.HasLicensedPlugins; // License: only when a plugin is sold with one
        ShowSitesColumn();
    }
}
