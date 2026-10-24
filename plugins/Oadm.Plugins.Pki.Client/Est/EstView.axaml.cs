using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Est.Client;

/// <summary>
/// Page view. View only: activates the view model while the page is shown, opens the certificate details it asks for and
/// passes a double click on an enrollment to View certificate.
/// </summary>
public partial class EstView : UserControl
{
    /// <summary>From this width of the page General and Profiles stand left of the enrollments; below it one column.</summary>
    public const double TwoColumnWidth = Oadm.Plugins.Pki.Client.Servers.ServerPageLayout.TwoColumnWidth;

    /// <summary>The height of the enrollments in one column (their grid stays bounded and virtualized).</summary>
    public const double OneColumnEnrollmentsHeight = 480;

    private EstViewModel? _vm;
    private int _columns;
    private Action? _unfollowTip;

    public EstView()
    {
        InitializeComponent();
        EnrollmentsGrid.DoubleTapped += OnEnrollmentDoubleTapped;
    }

    /// <summary>The columns of the page now: 2 or 1 (tests; 2 before the first layout, as in the XAML).</summary>
    public int ColumnCount => _columns == 0 ? 2 : _columns;

    /// <summary>The columns follow the width before the cards are measured.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            Arrange(availableSize.Width);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// Two columns (General above Profiles | the enrollments, as high as the page) or one (General and Profiles, then the
    /// enrollments in a row of <see cref="OneColumnEnrollmentsHeight"/>) by the width of the page. Layout only.
    /// </summary>
    private void Arrange(double width)
    {
        var columns = width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var two = columns == 2;
        PageColumns.ColumnDefinitions[0].Width = two ? new GridLength(Oadm.Plugins.Pki.Client.Servers.ServerPageLayout.LeftColumnWidth) : new GridLength(1, GridUnitType.Star);
        PageColumns.ColumnDefinitions[1].Width = new GridLength(two ? 16 : 0);
        PageColumns.ColumnDefinitions[2].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PageColumns.RowDefinitions[1].Height = two ? new GridLength(0) : new GridLength(OneColumnEnrollmentsHeight);
        Grid.SetRow(EnrollmentsCard, two ? 0 : 1);
        Grid.SetColumn(EnrollmentsCard, two ? 2 : 0);
        EnrollmentsCard.Margin = two ? default : new Thickness(0, 16, 0, 0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as EstViewModel;
        if (_vm is not null)
        {
            _vm.CertificateRequested += OnCertificateRequested;
            _unfollowTip = Oadm.Plugins.Pki.Client.Servers.PolicyLinesTip.Follow(PoliciesLink, _vm, nameof(EstViewModel.PolicyLines), () => _vm?.PolicyLines ?? []);
            _vm.Activate();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CertificateRequested -= OnCertificateRequested;
            _unfollowTip?.Invoke();
            _unfollowTip = null;
            _vm.Deactivate();
            _vm = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A profile row's ⋯ button: Remove in red (the shared row menu). View only.</summary>
    private void OnProfileActions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileRowViewModel row } button && _vm is not null)
        {
            RowActions.Show(button, [new("Remove", _vm.RemoveProfileCommand, row, IsDanger: true)]);
        }
    }

    private void OnEnrollmentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.ViewCertificateCommand.CanExecute(null) == true)
        {
            _vm.ViewCertificateCommand.Execute(null);
        }
    }

    private void OnCertificateRequested(object? sender, CertificateDetails certificate)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _ = CertificateWindow.ShowAsync(certificate, owner);
        }
    }
}
