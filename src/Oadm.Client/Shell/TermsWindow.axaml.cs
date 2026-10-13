using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>The terms of use on the first start. View only: closing the window without Accept counts as Quit.</summary>
public partial class TermsWindow : Window
{
    private bool _completed;

    public TermsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised once: true = accepted, false = quit (also the X).</summary>
    public event EventHandler<bool>? Completed;

    public void Attach(TermsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Completed += (_, accepted) => Finish(accepted);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finish(false);
    }

    private void Finish(bool accepted)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        Completed?.Invoke(this, accepted);
    }
}
