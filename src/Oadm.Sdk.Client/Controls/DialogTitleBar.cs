using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The header of every dialog window (a window with <c>WindowDecorations="BorderOnly"</c>, no system title bar): the
/// title as a heading and an X that closes the window, aligned with the content. Like a dialog on a web page it does
/// not move and is modal. Padding comes from the theme (<c>ui|DialogTitleBar</c>).
/// </summary>
public sealed class DialogTitleBar : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Text));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public DialogTitleBar()
    {
        _text.Classes.Add("cardTitle");
        var close = new Button { VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("toolbar");
        close.Classes.Add("iconOnly");
        close.Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("close") };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        Grid.SetColumn(close, 1);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _text, close } };
    }

    /// <summary>The dialog title.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
    }
}
