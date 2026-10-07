using System.Windows.Input;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

using Oadm.Client.Devices;
using Oadm.Client.LiveView;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// Live video card: header (icon + title, subtitle, source switch when the device has several
/// video sources, close button), a state chip with detail text
/// (codec, resolution, fps or the error), and the picture scaled to fit on a dark surface. Slides
/// in from the right when <see cref="IsOpen"/> becomes true. All looks come from the theme
/// (Border.card, Border.pill, Border.liveViewSurface, c|IconLabel.sectionTitle).
/// </summary>
public sealed class LiveViewPanel : UserControl
{
    /// <summary>Fly-in: 250 ms ease-out, 360 px from the right, fading in.</summary>
    public static readonly TimeSpan FlyInDuration = TimeSpan.FromMilliseconds(250);

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(Subtitle));
    public static readonly StyledProperty<string?> StateTextProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(StateText));
    public static readonly StyledProperty<PillKind> StateKindProperty = AvaloniaProperty.Register<LiveViewPanel, PillKind>(nameof(StateKind));
    public static readonly StyledProperty<string?> DetailTextProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(DetailText));
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<LiveViewPanel, IImage?>(nameof(Source));
    public static readonly StyledProperty<ICommand?> CloseCommandProperty = AvaloniaProperty.Register<LiveViewPanel, ICommand?>(nameof(CloseCommand));
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<LiveViewPanel, bool>(nameof(IsOpen));
    public static readonly StyledProperty<IEnumerable<LiveViewSourceOption>?> SourcesProperty =
        AvaloniaProperty.Register<LiveViewPanel, IEnumerable<LiveViewSourceOption>?>(nameof(Sources));
    public static readonly StyledProperty<bool> HasSourceChoiceProperty = AvaloniaProperty.Register<LiveViewPanel, bool>(nameof(HasSourceChoice));
    public static readonly StyledProperty<ICommand?> SelectSourceCommandProperty = AvaloniaProperty.Register<LiveViewPanel, ICommand?>(nameof(SelectSourceCommand));

    private const double FlyInOffset = 360;

    private readonly IconLabel _title = new();
    private readonly TextBlock _subtitle = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _pill = new();
    private readonly TextBlock _stateText = new();
    private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _placeholder = new() { Text = "Waiting for video", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new();
    private readonly Border _card = new();
    private readonly ItemsControl _sources = new();
    private readonly Border _sourceSwitch = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 8, 0) };

    public LiveViewPanel()
    {
        _title.Classes.Add("sectionTitle");
        _subtitle.Classes.Add("secondary");
        _detail.Classes.Add("secondary");
        _detail.Classes.Add("small");
        _placeholder.Classes.Add("secondary");
        _pill.Classes.Add("pill");
        _pill.Child = _stateText;

        _close.Classes.Add("toolbar");
        _close.Classes.Add("iconOnly");
        _close.Content = new OadmIcon();
        _close.VerticalAlignment = VerticalAlignment.Top;
        ToolTip.SetTip(_close, "Close live view (Esc)");

        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        // Source switch: one segment per view area / sensor / channel, labelled with its number.
        _sources.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 });
        _sources.ItemTemplate = new FuncDataTemplate<LiveViewSourceOption>((_, _) =>
        {
            var segment = new Button();
            segment.Classes.Add("segment");
            segment.Bind(ContentProperty, new Binding(nameof(LiveViewSourceOption.Label)));
            segment.Bind(ToolTip.TipProperty, new Binding(nameof(LiveViewSourceOption.Name)));
            segment.Bind(Button.CommandParameterProperty, new Binding("."));
            segment.BindClass("selected", new Binding(nameof(LiveViewSourceOption.IsSelected)), null!);
            segment.Bind(Button.CommandProperty, this.GetObservable(SelectSourceCommandProperty).ToBinding());
            return segment;
        });
        _sourceSwitch.Classes.Add("segmented");
        _sourceSwitch.Child = _sources;
        _sourceSwitch.IsVisible = false;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(titles);
        Grid.SetColumn(_sourceSwitch, 1);
        header.Children.Add(_sourceSwitch);
        Grid.SetColumn(_close, 2);
        header.Children.Add(_close);
        DockPanel.SetDock(header, Dock.Top);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 14, 0, 14) };
        status.Children.Add(_pill);
        status.Children.Add(_detail);
        DockPanel.SetDock(status, Dock.Top);

        var surface = new Border();
        surface.Classes.Add("liveViewSurface");
        var picture = new Panel();
        picture.Children.Add(_placeholder);
        picture.Children.Add(_image);
        surface.Child = picture;

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(status);
        dock.Children.Add(surface);

        _card.Classes.Add("card");
        _card.Child = dock;
        _card.RenderTransform = new TranslateTransform();
        Content = _card;
        IsVisible = false;
        Update();
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public string? StateText { get => GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }

    public PillKind StateKind { get => GetValue(StateKindProperty); set => SetValue(StateKindProperty, value); }

    public string? DetailText { get => GetValue(DetailTextProperty); set => SetValue(DetailTextProperty, value); }

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    public IEnumerable<LiveViewSourceOption>? Sources { get => GetValue(SourcesProperty); set => SetValue(SourcesProperty, value); }

    public bool HasSourceChoice { get => GetValue(HasSourceChoiceProperty); set => SetValue(HasSourceChoiceProperty, value); }

    public ICommand? SelectSourceCommand { get => GetValue(SelectSourceCommandProperty); set => SetValue(SelectSourceCommandProperty, value); }

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
        else if (change.Property == TitleProperty || change.Property == SubtitleProperty || change.Property == StateTextProperty
            || change.Property == StateKindProperty || change.Property == DetailTextProperty || change.Property == SourceProperty
            || change.Property == CloseCommandProperty || change.Property == SourcesProperty || change.Property == HasSourceChoiceProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _stateText.Text = StateText;
        _pill.IsVisible = !string.IsNullOrEmpty(StateText);
        _pill.Classes.Set("ok", StateKind == PillKind.Ok);
        _pill.Classes.Set("warn", StateKind == PillKind.Warning);
        _pill.Classes.Set("error", StateKind == PillKind.Error);
        _pill.Classes.Set("accent", StateKind == PillKind.Accent);
        _detail.Text = DetailText;
        _image.Source = Source;
        _placeholder.IsVisible = Source is null;
        _close.Command = CloseCommand;
        _sources.ItemsSource = Sources;
        _sourceSwitch.IsVisible = HasSourceChoice;
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
