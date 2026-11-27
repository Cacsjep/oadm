using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Client.Controls;

/// <summary>
/// The header of a navigation page: page title, the small grey side subtitle right of it (bottom aligned) and optional
/// content on the right (<see cref="Trailing"/>, e.g. a status chip, or the text <see cref="Status"/>). Used by the core plugin pages and the host pages
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

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<PageTitleBar, string?>(nameof(Status));

    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ContentControl _trailing = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = false };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _left;

    public PageTitleBar()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _title.Classes.Add("pageTitle");
        _subtitle.Classes.Add("pageSubtitle");
        _status.Classes.Add("pageSubtitle");
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_title);
        left.Children.Add(_subtitle);
        _left = left;
        SetColumn(_trailing, 1);
        SetColumn(_status, 1);
        Children.Add(left);
        Children.Add(_trailing);
        Children.Add(_status);
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

    /// <summary>
    /// A live status of the whole page as text at the right end ("12 of 22 on", the Plugins page), in the subtitle's look
    /// and on its baseline. Used instead of <see cref="Trailing"/>.
    /// </summary>
    public string? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
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
        if (change.Property == TitleProperty || change.Property == SubtitleProperty || change.Property == TrailingProperty
            || change.Property == StatusProperty)
        {
            Update();
        }
    }

    /// <summary>Puts the subtitle on the title's baseline (<see cref="Sdk.Client.Controls.SideSubtitle.Align"/>).</summary>
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var arranged = base.ArrangeOverride(arrangeSize);
        Sdk.Client.Controls.SideSubtitle.Align(_title, _subtitle);
        AlignStatus();
        return arranged;
    }

    private void Update()
    {
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _trailing.Content = Trailing;
        _status.Text = Status;
        _status.IsVisible = !string.IsNullOrEmpty(Status);
    }

    /// <summary>The status on the subtitle's baseline (the title's, raised like the subtitle), as a whole-pixel offset.</summary>
    private void AlignStatus()
    {
        if (!_status.IsVisible || _title.TextLayout.TextLines.Count == 0 || _status.TextLayout.TextLines.Count == 0)
        {
            return;
        }

        double titleBaseline = _left.Bounds.Y + _title.Bounds.Y + _title.Padding.Top + _title.TextLayout.TextLines[0].Baseline;
        double statusBaseline = _status.Bounds.Y + _status.Padding.Top + _status.TextLayout.TextLines[0].Baseline;
        double shift = Math.Round(titleBaseline - statusBaseline) - Sdk.Client.Controls.SideSubtitle.Raise;
        _status.RenderTransform = shift == 0 ? null : new Avalonia.Media.TranslateTransform(0, shift);
    }
}
