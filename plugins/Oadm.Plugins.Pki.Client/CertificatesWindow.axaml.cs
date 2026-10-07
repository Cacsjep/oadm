using System.ComponentModel;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// The installed certificates window (view and delete). View only: wiring to the view model; the grid shows
/// <see cref="CertificatesViewModel.VisibleRows"/> grouped by <see cref="CertificateRow.Group"/> (virtualized DataGrid).
/// </summary>
public partial class CertificatesWindow : Window
{
    public CertificatesWindow()
    {
        InitializeComponent();
    }

    /// <summary>Attaches the view model; the window closes with the payload when the view model completes.</summary>
    public void Attach(CertificatesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.UiThread = action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        };
        viewModel.Confirm = (title, message, confirm) => MessageWindow.ConfirmAsync(this, title, message, confirm);
        viewModel.CloseRequested += (_, payload) => Close(payload);
        viewModel.PropertyChanged += OnViewModelChanged;
        CertificateGrid.Columns[0].IsVisible = viewModel.DeleteMode; // the check box column (columns are outside the binding tree)
        ShowRows(viewModel);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CertificatesViewModel.VisibleRows) && sender is CertificatesViewModel viewModel)
        {
            ShowRows(viewModel);
        }
    }

    private void ShowRows(CertificatesViewModel viewModel)
    {
        var view = new DataGridCollectionView(viewModel.VisibleRows);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(CertificateRow.Group)));
        CertificateGrid.ItemsSource = view;
    }
}
