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
    private EstViewModel? _vm;

    public EstView()
    {
        InitializeComponent();
        EnrollmentsGrid.DoubleTapped += OnEnrollmentDoubleTapped;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = DataContext as EstViewModel;
        if (_vm is not null)
        {
            _vm.CertificateRequested += OnCertificateRequested;
            _vm.Activate();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CertificateRequested -= OnCertificateRequested;
            _vm.Deactivate();
            _vm = null;
        }

        base.OnDetachedFromVisualTree(e);
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
