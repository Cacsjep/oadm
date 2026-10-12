using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One tab of a page with tabs (Web View devices, Live View views): the title and a close button, the active one
/// underlined in the accent color (theme <c>Border.browserTab</c>). A press runs <see cref="ActivateCommand"/>.
/// </summary>
public sealed class PageTab : Border
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageTab, string?>(nameof(Title));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<PageTab, bool>(nameof(IsActive));

    public static readonly StyledProperty<ICommand?> ActivateCommandProperty =
        AvaloniaProperty.Register<PageTab, ICommand?>(nameof(ActivateCommand));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<PageTab, ICommand?>(nameof(CloseCommand));

    private readonly OadmIcon _closeIcon = new() { Width = 12, Height = 12 };

    public PageTab()
    {
        Classes.Add("browserTab");
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));
        var close = new Button { Content = _closeIcon, VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("toolbar");
        close.Classes.Add("iconOnly");
        ToolTip.SetTip(close, "Close tab");
        close.Bind(Button.CommandProperty, this.GetObservable(CloseCommandProperty));
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { title, close } };
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Icon.close", ActualThemeVariant, out var x) && x is Geometry g)
        {
            _closeIcon.Data = g;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            Classes.Set("active", IsActive);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ActivateCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
