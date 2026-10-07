using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// View of <see cref="AddressAssignmentViewModel"/>, shared by both network dialogs. Code-behind only shows the
/// host name column (DataGrid columns are not in the visual tree, so they cannot bind to the view model).
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
        if (e.PropertyName == nameof(AddressAssignmentViewModel.ShowHostName))
        {
            UpdateColumns();
        }
    }

    private void UpdateColumns()
    {
        if (this.FindControl<DataGrid>("Grid") is { } grid)
        {
            var column = grid.Columns.FirstOrDefault(c => Equals(c.Header, "New host name"));
            if (column is not null)
            {
                column.IsVisible = _model?.ShowHostName == true;
            }
        }
    }
}
