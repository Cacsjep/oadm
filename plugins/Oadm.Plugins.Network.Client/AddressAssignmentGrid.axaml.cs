using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// View of <see cref="AddressAssignmentViewModel"/>, shared by both network dialogs. Code-behind only shows the
/// IPv6 and host name columns (DataGrid columns are not in the visual tree, so they cannot bind to the view model).
/// </summary>
public sealed partial class AddressAssignmentGrid : UserControl
{
    private AddressAssignmentViewModel? _model;

    public AddressAssignmentGrid()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as AddressAssignmentViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        UpdateColumns();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AddressAssignmentViewModel.ShowHostName) or nameof(AddressAssignmentViewModel.ShowIpv6))
        {
            UpdateColumns();
        }
    }

    private void UpdateColumns()
    {
        if (this.FindControl<DataGrid>("Grid") is { } grid)
        {
            if (grid.Columns.FirstOrDefault(c => Equals(c.Header, "New host name")) is { } host)
            {
                host.IsVisible = _model?.ShowHostName == true;
            }

            if (grid.Columns.FirstOrDefault(c => Equals(c.Header, "New IPv6 address")) is { } ipv6)
            {
                ipv6.IsVisible = _model?.ShowIpv6 == true;
            }
        }
    }
}
