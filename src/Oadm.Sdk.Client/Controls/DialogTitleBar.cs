using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The header of every dialog window (a window with <c>WindowDecorations="BorderOnly"</c>, no system title bar): the
/// title as a heading, an optional small grey <see cref="Subtitle"/> right of it (like a page title), optional
/// <see cref="Actions"/> left of the X (flat command buttons, e.g. Read from a camera) and an X that closes the window,
/// aligned with the content. Like a dialog on a web page it does not move and is modal. Padding comes
/// from the theme (<c>ui|DialogTitleBar</c>).
/// </summary>
public sealed class DialogTitleBar : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Text));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Subtitle));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    public DialogTitleBar()
    {
        _text.Classes.Add("cardTitle");
        _subtitle.Classes.Add("pageSubtitle");
        var close = new Button { VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("toolbar");
        close.Classes.Add("iconOnly");
        close.Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("close") };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        Grid.SetColumn(_actions, 1);
        Grid.SetColumn(close, 2);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_text);
        left.Children.Add(_subtitle);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { left, _actions, close } };
        _actions.Children.CollectionChanged += (_, _) => _actions.IsVisible = _actions.Children.Count > 0;
        _actions.IsVisible = false;
    }

    /// <summary>
    /// Controls left of the X (SDK 0.15.0): flat command buttons of the whole dialog (<c>ui:ToolbarButton</c>, icon and
    /// text), such as Read from a camera of Text overlays. Never the dialog's main action, which stays in the footer.
    /// </summary>
    public Avalonia.Controls.Controls Actions => _actions.Children;

    /// <summary>The dialog title.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>One short grey line right of the title; optional.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
        else if (change.Property == SubtitleProperty)
        {
            _subtitle.Text = Subtitle;
            _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        SideSubtitle.Align(_text, _subtitle);
        return arranged;
    }
}
