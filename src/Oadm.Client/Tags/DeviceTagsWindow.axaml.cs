using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.Tags;

/// <summary>The Tags dialog window (see <see cref="DeviceTagsViewModel"/>). View only: wiring and focus.</summary>
public partial class DeviceTagsWindow : Window
{
    public DeviceTagsWindow()
    {
        InitializeComponent();
    }

    public void Attach(DeviceTagsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>"New tag" puts the cursor into the name field.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceTagsViewModel.IsAddingTag) && sender is DeviceTagsViewModel { IsAddingTag: true })
        {
            Dispatcher.UIThread.Post(() => NewTagNameBox.Focus());
        }
    }
}
