using Avalonia.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// Provisioning management. View only: wiring to the view model, the status watch while the window is open, a double click
/// edits, the editor over it.
/// </summary>
public partial class ProvisioningWindow : Window, IProvisioningUi, IDisposable
{
    private CancellationTokenSource? _watch;

    public ProvisioningWindow()
    {
        InitializeComponent();
        ProfileGrid.DoubleTapped += async (_, _) =>
        {
            if (DataContext is ProvisioningDialogViewModel vm && vm.EditCommand.CanExecute(null))
            {
                await vm.EditCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        };
    }

    /// <summary>Shows <paramref name="viewModel"/> and follows the server's status while the window is open.</summary>
    public void Attach(ProvisioningDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Ui = this;
        viewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) =>
        {
            _watch = new CancellationTokenSource();
            _ = viewModel.WatchAsync(_watch.Token);
        };
        Closed += (_, _) => Dispose();
    }

    /// <summary>Ends the status watch (the window closed).</summary>
    public void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        GC.SuppressFinalize(this);
    }

    public async Task ShowProfileEditorAsync(ProfileEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = new ProfileEditorWindow();
        window.Attach(editor);
        _ = editor.LoadAsync();
        await window.ShowDialog(this).ConfigureAwait(true);
    }
}
