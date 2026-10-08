using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Client.Controls;

/// <summary>
/// The header of a navigation page: page title, the small grey side subtitle right of it (bottom aligned) and optional
/// content on the right (<see cref="Trailing"/>, e.g. a status chip). Used by the core plugin pages and the host pages
/// Users, Credentials and About, so every page header looks the same.
/// </summary>
public sealed class PageTitleBar : Grid
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageTitleBar, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PageTitleBar, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<PageTitleBar, object?>(nameof(Trailing));

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ContentControl _trailing = new() { VerticalAlignment = VerticalAlignment.Center };

    public PageTitleBar()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _title.Classes.Add("pageTitle");
        _subtitle.Classes.Add("pageSubtitle");
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_title);
        left.Children.Add(_subtitle);
        SetColumn(_trailing, 1);
        Children.Add(left);
        Children.Add(_trailing);
        Update();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Content right of the title (a control, or a view model with a data template).</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == SubtitleProperty || change.Property == TrailingProperty)
        {
            Update();
        }
    }

    /// <summary>Pixels the subtitle sits above the title baseline.</summary>
    public const double SubtitleRaise = 2;

    /// <summary>
    /// Puts the subtitle on the title's baseline. Both are bottom aligned, but a 22 px line has more space below the
    /// baseline than a 12 px line, so bottom alignment alone leaves the subtitle a few pixels too low. The difference is
    /// applied as a whole-pixel render offset (no second layout pass, text stays crisp), then the subtitle is raised by
    /// <see cref="SubtitleRaise"/> (user decision: 2 px above the title baseline looks right).
    /// </summary>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var arranged = base.ArrangeOverride(arrangeSize);
        if (_subtitle.IsVisible && _title.TextLayout.TextLines.Count > 0 && _subtitle.TextLayout.TextLines.Count > 0)
        {
            double titleBaseline = _title.Bounds.Y + _title.Padding.Top + _title.TextLayout.TextLines[0].Baseline;
            double subtitleBaseline = _subtitle.Bounds.Y + _subtitle.Padding.Top + _subtitle.TextLayout.TextLines[0].Baseline;
            double shift = Math.Round(titleBaseline - subtitleBaseline) - SubtitleRaise;
            _subtitle.RenderTransform = shift == 0 ? null : new Avalonia.Media.TranslateTransform(0, shift);
        }

        return arranged;
    }

    private void Update()
    {
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _trailing.Content = Trailing;
    }
}
