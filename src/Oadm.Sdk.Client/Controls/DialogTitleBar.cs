using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The title bar of every dialog window (extended client area): the window title on the left,
/// draggable, caption buttons drawn by the platform decorations on the right. Height and padding
/// come from the theme (<c>ui|DialogTitleBar</c>).
/// </summary>
public sealed class DialogTitleBar : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Text));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public DialogTitleBar()
    {
        _text.Classes.Add("appName");
        Child = _text;
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);
    }

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
