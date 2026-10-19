using Avalonia;
using Avalonia.Controls;

namespace Oadm.SiteNode.App.Views;

/// <summary>The server form (mode, address, Connect / Save); view only.</summary>
public partial class ServerForm : UserControl
{
    /// <summary>The header's form: Connect is the one accent button of the window.</summary>
    public static readonly StyledProperty<bool> IsPrimaryProperty = AvaloniaProperty.Register<ServerForm, bool>(nameof(IsPrimary));

    /// <summary>The connection problem below the address field.</summary>
    public static readonly StyledProperty<string?> ProblemProperty = AvaloniaProperty.Register<ServerForm, string?>(nameof(Problem));

    /// <summary>The button's text: "Save" on the Status tab, the view model's "Connect" / "Save" in the header.</summary>
    public static readonly StyledProperty<string> SaveTextProperty = AvaloniaProperty.Register<ServerForm, string>(nameof(SaveText), "Save");

    public string SaveText
    {
        get => GetValue(SaveTextProperty);
        set => SetValue(SaveTextProperty, value);
    }

    public ServerForm()
    {
        InitializeComponent();
    }

    public bool IsPrimary
    {
        get => GetValue(IsPrimaryProperty);
        set => SetValue(IsPrimaryProperty, value);
    }

    public string? Problem
    {
        get => GetValue(ProblemProperty);
        set => SetValue(ProblemProperty, value);
    }
}
