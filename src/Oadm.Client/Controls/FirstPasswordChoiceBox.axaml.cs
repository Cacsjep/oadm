using Avalonia.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// The credential choice of a first password (see <see cref="Devices.FirstPasswordChoice"/>): right of the password fields of
/// the add page's first password editor and of the context menu's Set password. View only.
/// </summary>
public partial class FirstPasswordChoiceBox : UserControl
{
    public FirstPasswordChoiceBox()
    {
        InitializeComponent();
    }
}
