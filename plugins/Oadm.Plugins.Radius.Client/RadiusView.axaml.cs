using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// Page view. View only: activates the view model while shown, opens the switch, Advanced and policy dialogs and the
/// certificate window, and passes double clicks (switch and policy: Edit, log row: View certificate).
/// </summary>
public partial class RadiusView : UserControl, ISwitchDialogs, ILogDialogs, IAdvancedDialog, IPolicyDialogs
{
    /// <summary>
    /// From this width of the page the four cards Server, Trusted CAs, Switches and Policies stand side by side (Switches
    /// gets 1.6 and Policies 1.35 parts: their tables are the widest).
    /// </summary>
    public const double FourColumnWidth = 1640;

    /// <summary>From this width two columns: Server and Trusted CAs on the left, Switches and Policies on the right; below it one.</summary>
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
        PoliciesGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Policies.EditCommand.CanExecute(null) == true)
            {
                _vm.Policies.EditCommand.Execute(null);
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

    /// <summary>The columns of the cards now: 4, 2 or 1 (tests; 4 before the first layout, as in the XAML).</summary>
    public int CardColumnCount => _columns == 0 ? 4 : _columns;

    /// <summary>
    /// Four columns (Server | Trusted CAs | Switches | Policies), two (Server and Trusted CAs | Switches and Policies) or one
    /// by the width of the page (like the Permissions page). Layout only.
    /// </summary>
    private void Arrange(double width)
    {
        var columns = width >= FourColumnWidth ? 4 : width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var gap = new GridLength(16);
        var none = new GridLength(0);
        var definitions = CardColumns.ColumnDefinitions;
        definitions[0].Width = new GridLength(columns == 4 ? 0.95 : 1, GridUnitType.Star);
        definitions[1].Width = columns == 4 ? gap : none;
        definitions[2].Width = columns == 4 ? new GridLength(0.8, GridUnitType.Star) : none;
        definitions[3].Width = columns >= 2 ? gap : none;
        definitions[4].Width = columns == 4 ? new GridLength(1.75, GridUnitType.Star) : columns == 2 ? new GridLength(1.25, GridUnitType.Star) : none;
        definitions[5].Width = columns == 4 ? gap : none;
        definitions[6].Width = columns == 4 ? new GridLength(1.45, GridUnitType.Star) : none;
        var below = new Thickness(0, 16, 0, 0);
        switch (columns)
        {
            case 4:
                Place(ServerCard, 0, 0, default);
                Place(TrustedCasCard, 0, 2, default);
                Place(SwitchesCard, 0, 4, default);
                Place(PoliciesCard, 0, 6, default);
                break;
            case 2:
                Place(ServerCard, 0, 0, default);
                Place(TrustedCasCard, 1, 0, below);
                Place(SwitchesCard, 0, 4, default);
                Place(PoliciesCard, 1, 4, below);
                break;
            default:
                Place(ServerCard, 0, 0, default);
                Place(TrustedCasCard, 1, 0, below);
                Place(SwitchesCard, 2, 0, below);
                Place(PoliciesCard, 3, 0, below);
                break;
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

    public async Task<bool> ShowAdvancedAsync(AdvancedViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var window = new AdvancedWindow();
        var saved = false;
        window.Attach(dialog, result => saved = result);
        await ShowAsync(window).ConfigureAwait(true);
        return saved;
    }

    public async Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var window = PolicyFormView.CreateWindow(editor);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
        }

        window.Show();
        return false;
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
            _vm.Policies.Dialogs = this;
            _vm.AdvancedDialog = this;
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
