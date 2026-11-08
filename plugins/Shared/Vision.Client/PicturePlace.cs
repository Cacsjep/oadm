using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Shared.Vision.Client;

/// <summary>
/// Places its children on the picture itself, the rectangle <see cref="PictureFit.Fit"/> gives: on a wide screen a corner of
/// the picture, never of the black bars. Takes no room of its own and only its children catch the pointer, so everything else
/// of the picture stays the overlay's.
/// </summary>
public sealed class PicturePlace : Panel
{
    public static readonly StyledProperty<PictureResolution> PictureSizeProperty =
        AvaloniaProperty.Register<PicturePlace, PictureResolution>(nameof(PictureSize), PictureResolution.Default);

    static PicturePlace() => AffectsArrange<PicturePlace>(PictureSizeProperty);

    public PictureResolution PictureSize
    {
        get => GetValue(PictureSizeProperty);
        set => SetValue(PictureSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The picture size type is each plugin's own (a struct in one, a record in the other).
        var picture = PictureFit.Fit(finalSize, (object?)PictureSize is PictureResolution size ? PictureFit.AspectOf(size.Width, size.Height) : PictureFit.AspectOf(0, 0));
        foreach (var child in Children)
        {
            child.Arrange(picture);
        }

        return finalSize;
    }
}

/// <summary>
/// Pause and Resume in the picture's bottom left corner (docs/spec/ocr.md "Pause", docs/spec/vision-center.md "Page"): the one
/// button over the shapes, with the Paused chip and a ring while the server works. It never takes the focus, so Esc and the
/// arrow keys stay with the picture. Put it in a <see cref="PicturePlace"/>.
/// </summary>
public sealed class PicturePauseBar : Decorator
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<PicturePauseBar, ICommand?>(nameof(Command));

    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<PicturePauseBar, bool>(nameof(IsPaused));

    /// <summary>The chip and the ring show (paused, or a Pause or Resume on its way).</summary>
    public static readonly StyledProperty<bool> ShowStateProperty =
        AvaloniaProperty.Register<PicturePauseBar, bool>(nameof(ShowState));

    public static readonly StyledProperty<bool> IsWorkingProperty =
        AvaloniaProperty.Register<PicturePauseBar, bool>(nameof(IsWorking));

    /// <summary>"Pause" or "Resume".</summary>
    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<PicturePauseBar, string?>(nameof(Tip));

    /// <summary>The chip's tooltip while paused ("Saved readers keep reading.").</summary>
    public static readonly StyledProperty<string?> PausedTipProperty =
        AvaloniaProperty.Register<PicturePauseBar, string?>(nameof(PausedTip));

    public const string PausedText = "Paused";

    private readonly OadmIcon _pause = Icon("Icon.pause");
    private readonly OadmIcon _start = Icon("Icon.start");
    private readonly Border _state;

    public PicturePauseBar()
    {
        _start.IsVisible = false;
        Button = new Button { Name = "PauseButton", Focusable = false, Content = new Panel { Children = { _pause, _start } } };
        Button.Classes.Add("toolbar");
        Button.Classes.Add("iconOnly");
        ToolTip.SetShowOnDisabled(Button, true);
        var buttonBack = new Border { Child = Button };
        buttonBack.Classes.Add("videoOverlay");

        Ring = new ProgressRing { Name = "PauseRing", Width = 16, Height = 16, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        Chip = new StatusChip { Name = "PausedChip", Text = PausedText, IsWarning = true, IsVisible = false };
        _state = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Ring, Chip } },
        };
        _state.Classes.Add("videoOverlay");

        var bar = new StackPanel
        {
            Name = "PauseBar",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { buttonBack, _state },
        };
        bar.Classes.Add("videoCorner");
        Child = bar;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty)
        {
            Button.Command = Command;
        }
        else if (change.Property == TipProperty)
        {
            ToolTip.SetTip(Button, Tip);
        }
        else if (change.Property == PausedTipProperty)
        {
            ToolTip.SetTip(Chip, PausedTip);
        }
        else if (change.Property == IsPausedProperty)
        {
            _pause.IsVisible = !IsPaused;
            _start.IsVisible = IsPaused;
            Chip.IsVisible = IsPaused;
        }
        else if (change.Property == ShowStateProperty)
        {
            _state.IsVisible = ShowState;
        }
        else if (change.Property == IsWorkingProperty)
        {
            Ring.IsVisible = IsWorking;
        }
    }

    /// <summary>The button (tests press it).</summary>
    public Button Button { get; }

    public ProgressRing Ring { get; }

    public StatusChip Chip { get; }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsPaused
    {
        get => GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public bool ShowState
    {
        get => GetValue(ShowStateProperty);
        set => SetValue(ShowStateProperty, value);
    }

    public bool IsWorking
    {
        get => GetValue(IsWorkingProperty);
        set => SetValue(IsWorkingProperty, value);
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    public string? PausedTip
    {
        get => GetValue(PausedTipProperty);
        set => SetValue(PausedTipProperty, value);
    }

    private static OadmIcon Icon(string key)
    {
        var icon = new OadmIcon();
        icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }
}
