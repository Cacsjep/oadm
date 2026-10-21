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
/// Without a <see cref="CloseCommand"/> the tab has no close button (fixed tabs of a window, theme class <c>fixed</c>);
/// <see cref="Count"/> above 0 shows a count badge after the title (e.g. requests that wait).
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

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<PageTab, int>(nameof(Count));

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
        _close = close;
        _badge = new Border { Child = _count, IsVisible = false, Margin = new Thickness(4, 0, 0, 0) };
        _badge.Classes.Add("badge");
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { title, _badge, close } };
        UpdateClose();
    }

    private readonly Button _close;
    private readonly Border _badge;
    private readonly TextBlock _count = new();

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The tab title.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The shown tab: underlined.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Runs when the user clicks the tab.</summary>
    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    /// <summary>The number in the badge after the title; 0 or less hides the badge.</summary>
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Runs when the user clicks the X (or the middle button on the tab); null = a fixed tab without an X.</summary>
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
        else if (change.Property == CloseCommandProperty)
        {
            UpdateClose();
        }
        else if (change.Property == CountProperty)
        {
            _badge.IsVisible = Count > 0;
            _count.Text = Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    private void UpdateClose()
    {
        _close.IsVisible = CloseCommand is not null;
        Classes.Set("fixed", CloseCommand is null);
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
