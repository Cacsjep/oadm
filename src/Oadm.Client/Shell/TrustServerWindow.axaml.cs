using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>
/// "Trust this server?" (see <see cref="TrustServerViewModel"/>), shown on its own while the login window is hidden:
/// <see cref="Trusted"/> after Trust server; Cancel or the X leave it false.
/// </summary>
public partial class TrustServerWindow : Window
{
    public TrustServerWindow()
    {
        InitializeComponent();
        TrustButton.Click += (_, _) =>
        {
            Trusted = true;
            Close(true);
        };
        Footer.CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>The user clicked Trust server.</summary>
    public bool Trusted { get; private set; }
}
