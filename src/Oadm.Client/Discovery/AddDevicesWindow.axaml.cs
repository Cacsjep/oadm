using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.Discovery;

/// <summary>The add devices page window. Glue only: open/close and keyboard focus for the inputs.</summary>
public partial class AddDevicesWindow : Window
{
    private AddDevicesViewModel? _vm;

    public AddDevicesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as AddDevicesViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += OnCloseRequested;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm is null)
        {
            return;
        }

        // The technician types right away: focus the input of the mode.
        (_vm.IsRangeMode ? RangeFromBox : _vm.IsManualMode ? AddressBox : null)?.Focus();
        await _vm.OpenAsync();
    }

    private void OnCloseRequested(object? sender, bool added) => Close(added);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddDevicesViewModel.Editor) && _vm is not null)
        {
            TextBox? target = _vm.IsLoginEditorOpen ? LoginPasswordBox : _vm.IsPasswordEditorOpen ? NewPasswordBox : null;
            if (target is not null)
            {
                DiscoveredRowViewModel? row = _vm.EditorRow;
                Dispatcher.UIThread.Post(() =>
                {
                    if (row is not null)
                    {
                        DeviceList.ScrollIntoView(row, null); // keep the edited device visible above the editor
                    }

                    target.Focus();
                });
            }
        }
    }
}
