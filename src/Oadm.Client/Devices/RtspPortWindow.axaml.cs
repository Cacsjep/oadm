using Avalonia.Controls;

namespace Oadm.Client.Devices;

/// <summary>The "RTSP port in OADM" dialog (see <see cref="RtspPortViewModel"/>). View only: wiring to the view model.</summary>
public partial class RtspPortWindow : Window
{
    public RtspPortWindow()
    {
        InitializeComponent();
    }

    public void Attach(RtspPortViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => Close(ok);
    }

    /// <summary>Reads the current port, then focuses the field.</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is RtspPortViewModel vm)
        {
            await vm.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }

        PortBox.Focus();
        PortBox.SelectAll();
    }
}
