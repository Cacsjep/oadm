using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the switch dialog and the certificate window, and
/// passes double clicks (switch: Edit, log row: View certificate).
/// </summary>
public partial class RadiusView : UserControl, ISwitchDialogs, ILogDialogs
{
    /// <summary>From this width of the page the cards Server and Switches stand side by side (Switches gets 1.7 parts); below it one column.</summary>
    public const double TwoColumnWidth = 1000;

    private RadiusViewModel? _vm;
    private int _columns;

    public RadiusView()
    {
        InitializeComponent();
        SwitchesGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Switches.EditCommand.CanExecute(null) == true)
            {
                _vm.Switches.EditCommand.Execute(null);
            }
        };
        LogGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Log.ViewCertificateCommand.CanExecute(null) == true)
            {
                _vm.Log.ViewCertificateCommand.Execute(null);
            }
        };
    }

    /// <summary>
    /// The columns follow the width before the cards are measured: a grid measured once too narrow keeps its fixed columns
    /// shrunk (the Switches grid), so the cards never get a pass at a width they do not fit.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            Arrange(availableSize.Width);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>The columns of the cards now: 2 or 1 (tests; 2 before the first layout, as in the XAML).</summary>
    public int CardColumnCount => _columns == 0 ? 2 : _columns;

    /// <summary>Two columns (Server | Switches) or one by the width of the page (like the Permissions page). Layout only.</summary>
    private void Arrange(double width)
    {
        var columns = width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var definitions = CardColumns.ColumnDefinitions;
        definitions[0].Width = new GridLength(1, GridUnitType.Star);
        definitions[1].Width = columns == 2 ? new GridLength(16) : new GridLength(0);
        definitions[2].Width = columns == 2 ? new GridLength(1.7, GridUnitType.Star) : new GridLength(0);
        Place(ServerCard, 0, 0, default);
        if (columns == 2)
        {
            Place(SwitchesCard, 0, 2, default);
        }
        else
        {
            Place(SwitchesCard, 1, 0, new Thickness(0, 16, 0, 0));
        }
    }

    private static void Place(Control card, int row, int column, Thickness margin)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        card.Margin = margin;
    }

    public async Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var window = new SwitchWindow();
        var saved = false;
        window.Attach(dialog, result => saved = result);
        await ShowAsync(window).ConfigureAwait(true);
        return saved;
    }

    public Task ShowCertificateAsync(CertificateDetails certificate) =>
        TopLevel.GetTopLevel(this) is Window owner ? CertificateWindow.ShowAsync(certificate, owner) : Task.CompletedTask;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as RadiusViewModel;
        if (_vm is not null)
        {
            _vm.Switches.Dialogs = this;
            _vm.Log.Dialogs = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _vm?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task ShowAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
