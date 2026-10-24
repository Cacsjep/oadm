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
    /// <summary>From this width of the page Server and Switches stand left of the Sessions card; below it one column.</summary>
    public const double TwoColumnWidth = Oadm.Plugins.Pki.Client.Servers.ServerPageLayout.TwoColumnWidth;

    /// <summary>The lowest the page's cards get in two columns, and the Sessions card in one; a lower page scrolls.</summary>
    public const double MinSessionsHeight = 480;

    private RadiusViewModel? _vm;
    private int _columns;
    private Action? _unfollowTip;

    public RadiusView()
    {
        InitializeComponent();
        PageScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
            {
                FitSessions();
            }
        };
        TopCards.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                PlaceSessions();
            }
        };
        SwitchesGrid.DoubleTapped += (_, _) =>
        {
            if (_vm?.Switches.EditCommand.CanExecute(null) == true)
            {
                _vm.Switches.EditCommand.Execute(null);
            }
        };
        SessionsGrid.DoubleTapped += (_, _) =>
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

    /// <summary>
    /// Two columns (Server above Switches | Sessions) or one (Server, Switches, Sessions below each other) by the width of
    /// the page; Sessions takes the whole width when Server and Switches are hidden (users limited to a device group).
    /// Layout only.
    /// </summary>
    private void Arrange(double width)
    {
        var columns = width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var definitions = PageRows.ColumnDefinitions;
        definitions[0].Width = columns == 2 ? new GridLength(Oadm.Plugins.Pki.Client.Servers.ServerPageLayout.LeftColumnWidth) : new GridLength(1, GridUnitType.Star);
        definitions[1].Width = columns == 2 ? new GridLength(16) : new GridLength(0);
        definitions[2].Width = columns == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PageRows.RowDefinitions[0].Height = columns == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        PageRows.RowDefinitions[1].Height = columns == 2 ? new GridLength(0) : new GridLength(MinSessionsHeight);
        CardColumns.RowDefinitions[2].Height = columns == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SwitchesCard.MinHeight = columns == 2 ? 0 : 320;
        PlaceSessions();
        FitSessions();
    }

    /// <summary>Sessions right of Server and Switches, below them, or alone over the whole width.</summary>
    private void PlaceSessions()
    {
        // In one column Sessions has its own row of a fixed height (its grid stays bounded and virtualized).
        var alone = !TopCards.IsVisible;
        var two = CardColumnCount == 2;
        Grid.SetRow(SessionsCard, two ? 0 : 1);
        Grid.SetColumn(SessionsCard, two && !alone ? 2 : 0);
        Grid.SetColumnSpan(SessionsCard, two && !alone ? 1 : 3);
        SessionsCard.Margin = two || alone ? default : new Thickness(0, 16, 0, 0);
    }

    /// <summary>
    /// In two columns the cards are as high as the page (at least <see cref="MinSessionsHeight"/>): the grids stay bounded
    /// and virtualized; in one column the page grows and scrolls. Layout only.
    /// </summary>
    private void FitSessions()
    {
        var viewport = PageScroll.Viewport.Height;
        if (CardColumnCount == 1 || !double.IsFinite(viewport) || viewport <= 0)
        {
            PageRows.Height = double.NaN;
            return;
        }

        var height = Math.Max(MinSessionsHeight, Math.Floor(viewport));
        if (double.IsNaN(PageRows.Height) || Math.Abs(PageRows.Height - height) >= 1)
        {
            PageRows.Height = height;
        }
    }

    /// <summary>A switch row's ⋯ button: Edit, then Delete in red (the shared row menu). View only.</summary>
    private void OnSwitchActions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SwitchRowViewModel row } button && _vm is not null)
        {
            RowActions.Show(button,
            [
                new("Edit", _vm.Switches.EditRowCommand, row),
                IssuedCertificateAction.Separator,
                new("Delete", _vm.Switches.DeleteRowCommand, row, IsDanger: true),
            ]);
        }
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
        _unfollowTip?.Invoke();
        _unfollowTip = null;
        _vm = DataContext as RadiusViewModel;
        if (_vm is not null)
        {
            var vm = _vm;
            _unfollowTip = Oadm.Plugins.Pki.Client.Servers.PolicyLinesTip.Follow(PoliciesLink, vm, nameof(RadiusViewModel.PolicyLines), () => vm.PolicyLines);
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
