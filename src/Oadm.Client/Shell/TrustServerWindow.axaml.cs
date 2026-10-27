using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>"Trust this server?" (see <see cref="TrustServerViewModel"/>): closes with true on Trust server.</summary>
public partial class TrustServerWindow : Window
{
    public TrustServerWindow()
    {
        InitializeComponent();
        TrustButton.Click += (_, _) => Close(true);
        Footer.CancelButton.Click += (_, _) => Close(false);
    }
}
