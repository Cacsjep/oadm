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
        }

        _vm = DataContext as PluginsViewModel;
        if (_vm is not null)
        {
            _vm.Plugins.CollectionChanged += OnPluginsChanged;
        }

        Regroup();
        base.OnDataContextChanged(e);
    }

    private void OnPluginsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Regroup();

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
    }
}
