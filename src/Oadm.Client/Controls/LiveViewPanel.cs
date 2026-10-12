using System.Windows.Input;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

using Oadm.Client.LiveView;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// The live view side panel: a card with one header row (video icon + the camera's name, close button) and the shared
/// live video below it (<see cref="LiveVideoView"/>: the picture and everything over it). Slides in from the right when
/// <see cref="IsOpen"/> becomes true. All looks come from the theme (Border.card, c|IconLabel.sectionTitle).
/// </summary>
public sealed class LiveViewPanel : UserControl
{
    /// <summary>Fly-in: 250 ms ease-out, 360 px from the right, fading in.</summary>
    public static readonly TimeSpan FlyInDuration = TimeSpan.FromMilliseconds(250);

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(Title));
    public static readonly StyledProperty<ICommand?> CloseCommandProperty = AvaloniaProperty.Register<LiveViewPanel, ICommand?>(nameof(CloseCommand));
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<LiveViewPanel, bool>(nameof(IsOpen));
    public static readonly StyledProperty<LiveVideoViewModel?> VideoProperty = AvaloniaProperty.Register<LiveViewPanel, LiveVideoViewModel?>(nameof(Video));

    private const double FlyInOffset = 360;

    private readonly IconLabel _title = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _card = new();
    private readonly LiveVideoView _video = new();

    public LiveViewPanel()
    {
        _title.Classes.Add("sectionTitle");
        _close.Classes.Add("toolbar");
        _close.Classes.Add("iconOnly");
        _close.Content = new OadmIcon();
        ToolTip.SetTip(_close, "Close live preview (Esc)");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_title);
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);
        DockPanel.SetDock(header, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(_video);

        _card.Classes.Add("card");
        _card.Child = dock;
        _card.RenderTransform = new TranslateTransform();
        Content = _card;
        IsVisible = false;
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>The live video shown below the header.</summary>
    public LiveVideoViewModel? Video { get => GetValue(VideoProperty); set => SetValue(VideoProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Icon.video", ActualThemeVariant, out var video) && video is Geometry v)
        {
            _title.Icon = v;
        }

        if (_close.Content is OadmIcon icon && this.TryFindResource("Icon.close", ActualThemeVariant, out var close) && close is Geometry c)
        {
            icon.Data = c;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            IsVisible = IsOpen;
            if (IsOpen)
            {
                _ = FlyInAsync();
            }
        }
        else if (change.Property == TitleProperty)
        {
            _title.Text = Title;
        }
        else if (change.Property == CloseCommandProperty)
        {
            _close.Command = CloseCommand;
        }
        else if (change.Property == VideoProperty)
        {
            _video.DataContext = Video;
        }
    }

    private Task FlyInAsync()
    {
        var animation = new Animation
        {
            Duration = FlyInDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(TranslateTransform.XProperty, FlyInOffset), new Setter(OpacityProperty, 0.0) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(TranslateTransform.XProperty, 0.0), new Setter(OpacityProperty, 1.0) },
                },
            },
        };
        return animation.RunAsync(_card);
    }
}
