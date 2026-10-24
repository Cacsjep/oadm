using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the switch dialog, the certificate choice, the
/// revoke confirmation and the certificate window, and passes double clicks (switch: Edit, log row: View certificate).
/// </summary>
public partial class RadiusView : UserControl, ISwitchDialogs, ILogDialogs, IRevokeDialogs
{
    /// <summary>
    /// From this width of the page the cards General, Switches and VLANs stand side by side: each card's widest row (the
    /// Switches columns, a VLAN rule with its stretching site or tag) needs about 470 px plus the card padding (Switches gets
    /// 1.25 parts of the width), so a Full HD screen at 100 % keeps three columns.
    /// </summary>
    public const double ThreeColumnWidth = 1660;

    /// <summary>
    /// From this width two columns: General with VLANs below it on the left, Switches on the right; below it one column
    /// (General, Switches, VLANs).
    /// </summary>
    public const double TwoColumnWidth = 1100;

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

    /// <summary>The columns of the Server tab now: 3, 2 or 1 (tests; 3 before the first layout, as in the XAML).</summary>
    public int ServerColumnCount => _columns == 0 ? 3 : _columns;

    /// <summary>
    /// Three columns (General | Switches | VLANs), two (General and VLANs | Switches) or one (General, Switches, VLANs) by
    /// the width of the Server tab (like the Permissions page). Layout only.
    /// </summary>
    private void Arrange(double width)
    {
        var columns = width >= ThreeColumnWidth ? 3 : width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var star = new GridLength(1, GridUnitType.Star);
        var gap = new GridLength(16);
        var none = new GridLength(0);
        var definitions = ServerColumns.ColumnDefinitions;
        definitions[1].Width = columns >= 2 ? gap : none;
        definitions[2].Width = columns == 3 ? new GridLength(1.25, GridUnitType.Star) : columns == 2 ? star : none; // Switches has the widest table
        definitions[3].Width = columns == 3 ? gap : none;
        definitions[4].Width = columns == 3 ? star : none;
        var below = new Thickness(0, 16, 0, 0);
        switch (columns)
        {
            case 3:
                Place(GeneralCard, 0, 0, 1, default);
                Place(SwitchesCard, 0, 2, 1, default);
                Place(VlansCard, 0, 4, 1, default);
                break;
            case 2:
                Place(GeneralCard, 0, 0, 1, default);
                Place(VlansCard, 1, 0, 1, below);
                Place(SwitchesCard, 0, 2, 2, default);
                break;
            default:
                Place(GeneralCard, 0, 0, 1, default);
                Place(SwitchesCard, 1, 0, 1, below);
                Place(VlansCard, 2, 0, 1, below);
                break;
        }
    }

    private static void Place(Control card, int row, int column, int rowSpan, Thickness margin)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetRowSpan(card, rowSpan);
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

    public async Task<IReadOnlyList<IssuedCertificateRow>?> ChooseCertificatesAsync(RevokePickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        var window = new RevokePickerWindow();
        IReadOnlyList<IssuedCertificateRow>? chosen = null;
        window.Attach(picker, rows => chosen = rows);
        window.Opened += (_, _) => _ = picker.LoadAsync();
        await ShowAsync(window).ConfigureAwait(true);
        return chosen;
    }

    public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message) =>
        TopLevel.GetTopLevel(this) is Window owner ? IssuedCertificateList.ConfirmRevokeAsync(owner, title, message) : Task.FromResult<RevocationReason?>(null);

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
            _vm.Revoked.Dialogs = this;
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
