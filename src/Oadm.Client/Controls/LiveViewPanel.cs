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
/// Live video card: one header row (icon + title, the detail text with codec, resolution and fps or
/// the reason, the state chip while not live, the source switch when the device has several video
/// sources, close button), and the picture scaled to fit on a dark surface. Over the bottom of the
/// picture the zoom and focus bar (<see cref="Optics"/>, only for cameras that have them) and, while
/// the autofocus area is edited, its frame (<see cref="FocusAreaOverlay"/>). Slides in from the right
/// when <see cref="IsOpen"/> becomes true. All looks come from the theme (Border.card, Border.pill,
/// Border.liveViewSurface, Border.opticsBar, c|IconLabel.sectionTitle).
/// </summary>
public sealed class LiveViewPanel : UserControl
{
    /// <summary>Fly-in: 250 ms ease-out, 360 px from the right, fading in.</summary>
    public static readonly TimeSpan FlyInDuration = TimeSpan.FromMilliseconds(250);

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<LiveViewPanel, string?>(nameof(Title));
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
    public static readonly StyledProperty<OpticsViewModel?> OpticsProperty = AvaloniaProperty.Register<LiveViewPanel, OpticsViewModel?>(nameof(Optics));
    public static readonly StyledProperty<PtzViewModel?> PtzProperty = AvaloniaProperty.Register<LiveViewPanel, PtzViewModel?>(nameof(Ptz));

    private const double FlyInOffset = 360;

    private readonly IconLabel _title = new();
    private readonly Border _pill = new();
    private readonly TextBlock _stateText = new();
    private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _placeholder = new() { Text = "Waiting for video", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new();
    private readonly Border _card = new();
    private readonly ItemsControl _sources = new();
    private readonly Border _sourceSwitch = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
    private readonly OpticsBar _opticsBar = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly FocusAreaOverlay _focusArea = new() { IsVisible = false };
    private readonly PtzOverlay _ptzOverlay = new() { IsVisible = false };
    private readonly PtzBar _ptzBar = new() { VerticalAlignment = VerticalAlignment.Bottom };

    public LiveViewPanel()
    {
        _title.Classes.Add("sectionTitle");
        _detail.Classes.Add("secondary");
        _detail.Classes.Add("small");
        _placeholder.Classes.Add("secondary");
        _pill.Classes.Add("pill");
        _pill.Child = _stateText;

        _close.Classes.Add("toolbar");
        _close.Classes.Add("iconOnly");
        _close.Content = new OadmIcon();
        _close.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_close, "Close live view (Esc)");

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

        // One row: title, then codec / resolution / fps (or the reason) and the state right of it, source switch, close.
        _detail.VerticalAlignment = VerticalAlignment.Center;
        _detail.HorizontalAlignment = HorizontalAlignment.Right;
        _detail.Margin = new Thickness(12, 0, 0, 0);
        _pill.VerticalAlignment = VerticalAlignment.Center;
        _pill.Margin = new Thickness(10, 0, 0, 0);
        _title.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_title);
        Grid.SetColumn(_detail, 1);
        header.Children.Add(_detail);
        Grid.SetColumn(_pill, 2);
        header.Children.Add(_pill);
        Grid.SetColumn(_sourceSwitch, 3);
        header.Children.Add(_sourceSwitch);
        Grid.SetColumn(_close, 4);
        header.Children.Add(_close);
        DockPanel.SetDock(header, Dock.Top);

        var surface = new Border();
        surface.Classes.Add("liveViewSurface");
        var picture = new Panel();
        picture.Children.Add(_placeholder);
        picture.Children.Add(_image);
        picture.Children.Add(_focusArea);
        picture.Children.Add(_ptzOverlay);
        picture.Children.Add(_opticsBar);
        picture.Children.Add(_ptzBar);
        surface.Child = picture;
        _focusArea.Bind(FocusAreaOverlay.AreaProperty, new Binding($"{nameof(Optics)}.{nameof(OpticsViewModel.Area)}") { Source = this, Mode = BindingMode.TwoWay });
        _focusArea.Bind(IsVisibleProperty, new Binding($"{nameof(Optics)}.{nameof(OpticsViewModel.IsEditingArea)}") { Source = this, FallbackValue = false });
        _opticsBar.Bind(IsVisibleProperty, new Binding($"{nameof(Optics)}.{nameof(OpticsViewModel.IsVisible)}") { Source = this, FallbackValue = false });
        _ptzOverlay.Bind(IsVisibleProperty, new Binding($"{nameof(Ptz)}.{nameof(PtzViewModel.IsVisible)}") { Source = this, FallbackValue = false });
        _ptzBar.Bind(IsVisibleProperty, new Binding($"{nameof(Ptz)}.{nameof(PtzViewModel.IsVisible)}") { Source = this, FallbackValue = false });

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(surface);

        _card.Classes.Add("card");
        _card.Child = dock;
        _card.RenderTransform = new TranslateTransform();
        Content = _card;
        IsVisible = false;
        Update();
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? StateText { get => GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }

    public PillKind StateKind { get => GetValue(StateKindProperty); set => SetValue(StateKindProperty, value); }

    public string? DetailText { get => GetValue(DetailTextProperty); set => SetValue(DetailTextProperty, value); }

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    public IEnumerable<LiveViewSourceOption>? Sources { get => GetValue(SourcesProperty); set => SetValue(SourcesProperty, value); }

    public bool HasSourceChoice { get => GetValue(HasSourceChoiceProperty); set => SetValue(HasSourceChoiceProperty, value); }

    public ICommand? SelectSourceCommand { get => GetValue(SelectSourceCommandProperty); set => SetValue(SelectSourceCommandProperty, value); }

    /// <summary>Zoom and focus of the shown camera; the bar shows only while <see cref="OpticsViewModel.IsVisible"/>.</summary>
    public OpticsViewModel? Optics { get => GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }

    /// <summary>PTZ of the shown camera: the circle and the bar show only while <see cref="PtzViewModel.IsVisible"/>.</summary>
    public PtzViewModel? Ptz { get => GetValue(PtzProperty); set => SetValue(PtzProperty, value); }

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
        else if (change.Property == OpticsProperty)
        {
            _opticsBar.DataContext = Optics;
        }
        else if (change.Property == PtzProperty)
        {
            _ptzBar.DataContext = Ptz;
            _ptzOverlay.Ptz = Ptz;
        }
        else if (change.Property == TitleProperty || change.Property == StateTextProperty
            || change.Property == StateKindProperty || change.Property == DetailTextProperty || change.Property == SourceProperty
            || change.Property == CloseCommandProperty || change.Property == SourcesProperty || change.Property == HasSourceChoiceProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _title.Text = Title;
        _stateText.Text = StateText;
        _pill.IsVisible = !string.IsNullOrEmpty(StateText);
        _pill.Classes.Set("ok", StateKind == PillKind.Ok);
        _pill.Classes.Set("warn", StateKind == PillKind.Warning);
        _pill.Classes.Set("error", StateKind == PillKind.Error);
        _pill.Classes.Set("accent", StateKind == PillKind.Accent);
        _detail.Text = DetailText;
        ToolTip.SetTip(_detail, string.IsNullOrEmpty(DetailText) ? null : DetailText);
        _image.Source = Source;
        _focusArea.PictureSize = Source?.Size ?? default;
        _ptzOverlay.PictureSize = Source?.Size ?? default;
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
