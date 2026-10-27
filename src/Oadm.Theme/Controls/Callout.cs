using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Theme.Controls;

/// <summary>What a <see cref="Callout"/> reports.</summary>
public enum CalloutKind
{
    /// <summary>Nothing can go on until it is solved (server not reachable, login locked).</summary>
    Error,

    /// <summary>Needs a decision (the server certificate changed).</summary>
    Warning,

    /// <summary>A short note (the session ended).</summary>
    Info,
}

/// <summary>
/// A problem that stops a whole form, shown directly below the field it belongs to (the login's server and password):
/// error and warning centered with the icon on top, the <see cref="Title"/>, the <see cref="Text"/> and an optional
/// <see cref="Action"/> button; info as one line with the icon left. Colors from the theme (<c>tc|Callout</c> with the
/// classes <c>error</c>, <c>warning</c>, <c>info</c>).
/// </summary>
public sealed class Callout : Border
{
    public static readonly StyledProperty<CalloutKind> KindProperty =
        AvaloniaProperty.Register<Callout, CalloutKind>(nameof(Kind));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Callout, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Callout, string?>(nameof(Text));

    public static readonly StyledProperty<Control?> ActionProperty =
        AvaloniaProperty.Register<Callout, Control?>(nameof(Action));

    private readonly OadmIcon _icon = new();
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _action = new();
    private readonly StackPanel _stack = new();

    public Callout()
    {
        _title.Classes.Add("calloutTitle");
        _text.Classes.Add("calloutText");
        _action.Classes.Add("calloutAction");
        _stack.Children.Add(_icon);
        _stack.Children.Add(_title);
        _stack.Children.Add(_text);
        _stack.Children.Add(_action);
        Child = _stack;
        Update();
    }

    public CalloutKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The heading ("Cannot reach the server"); not shown for <see cref="CalloutKind.Info"/>.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>A button below the text (Try again, Forget server).</summary>
    public Control? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == TitleProperty || change.Property == TextProperty || change.Property == ActionProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        bool info = Kind == CalloutKind.Info;
        Classes.Set("error", Kind == CalloutKind.Error);
        Classes.Set("warning", Kind == CalloutKind.Warning);
        Classes.Set("info", info);
        _icon.Data = ToolbarButton.ResolveIcon(Kind switch
        {
            CalloutKind.Error => "status.error",
            CalloutKind.Warning => "status.warning",
            _ => "status.info",
        });
        _stack.Orientation = info ? Orientation.Horizontal : Orientation.Vertical;
        _stack.HorizontalAlignment = info ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        _title.Text = Title;
        _title.IsVisible = !info && !string.IsNullOrEmpty(Title);
        _text.Text = Text;
        _text.TextAlignment = info ? TextAlignment.Left : TextAlignment.Center;
        _action.Content = Action;
        _action.IsVisible = !info && Action is not null;
    }
}
