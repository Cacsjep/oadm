using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Plugins.Pki.Client.Servers;

/// <summary>The off state of a server's tab (<see cref="ServerSwitchViewModel"/>): its icon and what it does. View only.</summary>
public partial class ServerOffStateView : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ServerOffStateView, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ServerOffStateView, string?>(nameof(Text));

    public ServerOffStateView()
    {
        InitializeComponent();
    }

    /// <summary>The server's icon.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What the server does, one short sentence.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
