using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The picture of the last reading with the reader's areas and the found lines drawn on it (docs/spec/ocr.md "Page"): areas
/// dashed in the accent colour with their name on a tag, the selected one solid; the lines as teal boxes with their text.
/// Keeps the picture's shape inside the space it gets. While <see cref="IsDrawing"/> a drag draws a rectangle that is handed
/// to <see cref="AreaDrawn"/>; a click selects the area under it (<see cref="AreaClicked"/>). Colours and fonts come from the
/// theme (like the charts); no logic beyond the geometry.
/// </summary>
public sealed class ReadingPicture : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ReadingPicture, IImage?>(nameof(Source));

    public static readonly StyledProperty<Resolution> PictureSizeProperty =
        AvaloniaProperty.Register<ReadingPicture, Resolution>(nameof(PictureSize), Resolution.Default);

    public static readonly StyledProperty<IReadOnlyList<AreaViewModel>?> AreasProperty =
        AvaloniaProperty.Register<ReadingPicture, IReadOnlyList<AreaViewModel>?>(nameof(Areas));

    public static readonly StyledProperty<IReadOnlyList<TextLine>?> LinesProperty =
        AvaloniaProperty.Register<ReadingPicture, IReadOnlyList<TextLine>?>(nameof(Lines));

    public static readonly StyledProperty<bool> IsDrawingProperty =
        AvaloniaProperty.Register<ReadingPicture, bool>(nameof(IsDrawing));

    /// <summary>Changes whenever the areas, the selection or the lines changed: the overlay draws again.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<ReadingPicture, int>(nameof(Version));

    /// <summary>The smallest area drawn, relative to the picture (a click is not an area).</summary>
    public const double MinAreaSize = 0.02;

    private Point? _dragStart;
    private Point _dragNow;

    static ReadingPicture()
    {
        AffectsRender<ReadingPicture>(SourceProperty, AreasProperty, LinesProperty, IsDrawingProperty, VersionProperty);
        AffectsMeasure<ReadingPicture>(PictureSizeProperty);
        FocusableProperty.OverrideDefaultValue<ReadingPicture>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ReadingPicture>(true);
    }

    /// <summary>A rectangle was drawn, relative to the picture.</summary>
    public event EventHandler<RelativeBox>? AreaDrawn;

    /// <summary>A click on the picture (not drawing), relative to the picture.</summary>
    public event EventHandler<Point>? AreaClicked;

    /// <summary>Esc while drawing.</summary>
    public event EventHandler? DrawingCancelled;

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Resolution PictureSize
    {
        get => GetValue(PictureSizeProperty);
        set => SetValue(PictureSizeProperty, value);
    }

    public IReadOnlyList<AreaViewModel>? Areas
    {
        get => GetValue(AreasProperty);
        set => SetValue(AreasProperty, value);
    }

    public IReadOnlyList<TextLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public bool IsDrawing
    {
        get => GetValue(IsDrawingProperty);
        set => SetValue(IsDrawingProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>Where the picture sits inside the control (its shape kept, centered).</summary>
    public Rect PictureRect => Fit(Bounds.Size, Aspect);

    private double Aspect => PictureSize.Width > 0 && PictureSize.Height > 0 ? (double)PictureSize.Width / PictureSize.Height : 16.0 / 9;

    /// <summary>A point of the control relative to the picture (0 to 1 each, clamped).</summary>
    public Point ToRelative(Point point)
    {
        var picture = PictureRect;
        return picture.Width <= 0 || picture.Height <= 0
            ? default
            : new Point(Math.Clamp((point.X - picture.X) / picture.Width, 0, 1), Math.Clamp((point.Y - picture.Y) / picture.Height, 0, 1));
    }

    /// <summary>The rectangle between two relative points.</summary>
    public static RelativeBox BoxBetween(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Finishes a drag from <paramref name="start"/> to <paramref name="end"/> (relative): an area or nothing.</summary>
    public void FinishDrag(Point start, Point end)
    {
        var box = BoxBetween(start, end);
        if (box.Width >= MinAreaSize && box.Height >= MinAreaSize)
        {
            AreaDrawn?.Invoke(this, box);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = width / Aspect;
        if (!double.IsInfinity(availableSize.Height) && height > availableSize.Height)
        {
            height = availableSize.Height;
            width = height * Aspect;
        }

        return new Size(width, height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var relative = ToRelative(e.GetPosition(this));
        if (!IsDrawing)
        {
            AreaClicked?.Invoke(this, relative);
            return;
        }

        _dragStart = relative;
        _dragNow = relative;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (_dragStart is null)
        {
            return;
        }

        _dragNow = ToRelative(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
        {
            return;
        }

        _dragStart = null;
        e.Pointer.Capture(null);
        FinishDrag(start, ToRelative(e.GetPosition(this)));
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsDrawing)
        {
            _dragStart = null;
            DrawingCancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsDrawingProperty && !IsDrawing)
        {
            _dragStart = null;
        }
    }

    /// <summary>Shows a drag in progress (tests: the drawing screenshot without a pointer).</summary>
    internal void ShowDrag(Point start, Point now)
    {
        _dragStart = start;
        _dragNow = now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var picture = PictureRect;
        if (picture.Width <= 0 || picture.Height <= 0)
        {
            return;
        }

        context.FillRectangle(Brush("Oadm.VideoBackgroundBrush"), picture);
        if (Source is { } source)
        {
            context.DrawImage(source, new Rect(source.Size), picture);
        }

        using (context.PushClip(picture))
        {
            DrawLines(context, picture);
            DrawAreas(context, picture);
            if (_dragStart is { } start)
            {
                var accent = Brush("Oadm.AccentBrush");
                var rect = ToRect(BoxBetween(start, _dragNow), picture);
                context.DrawRectangle(Tint(accent, 0.16), new Pen(accent, 2), rect);
            }
        }

        if (IsDrawing)
        {
            DrawHint(context, picture);
        }
    }

    private void DrawLines(DrawingContext context, Rect picture)
    {
        if (Lines is not { Count: > 0 } lines)
        {
            return;
        }

        var teal = Brush("Oadm.TealBrush");
        var pen = new Pen(teal, 1.5);
        var font = Font("Oadm.FontFamilyMono");
        var size = Number("Oadm.FontSizeSmall", 11);
        var label = Brush("Oadm.OverlayBrush");
        foreach (var line in lines)
        {
            var rect = ToRect(line.Box, picture);
            context.DrawRectangle(null, pen, rect);
            if (line.Text.Length == 0)
            {
                continue;
            }

            var text = new FormattedText(line.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, teal);
            var at = new Point(rect.X, rect.Bottom + 2);
            if (at.Y + text.Height > picture.Bottom)
            {
                at = new Point(rect.X, rect.Y - text.Height - 2);
            }

            context.FillRectangle(label, new Rect(at.X - 2, at.Y - 1, text.Width + 4, text.Height + 2), 2);
            context.DrawText(text, at);
        }
    }

    private void DrawAreas(DrawingContext context, Rect picture)
    {
        if (Areas is not { Count: > 0 } areas)
        {
            return;
        }

        var accent = Brush("Oadm.AccentBrush");
        var onAccent = Brush("Oadm.TextOnAccentBrush");
        var dashed = new Pen(accent, 2, new DashStyle([4, 3], 0));
        var solid = new Pen(accent, 2);
        var font = Font("Oadm.FontFamily");
        var size = Number("Oadm.FontSizeSmall", 11);
        foreach (var area in areas)
        {
            var rect = ToRect(area.Box, picture);
            context.DrawRectangle(Tint(accent, area.IsSelected ? 0.16 : 0.08), area.IsSelected ? solid : dashed, rect);
            var name = string.IsNullOrWhiteSpace(area.Name) ? " " : area.Name.Trim();
            var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, onAccent);
            var tag = new Rect(rect.X - 1, rect.Y - text.Height - 4, text.Width + 10, text.Height + 4);
            if (tag.Y < picture.Y)
            {
                tag = tag.WithY(rect.Y + 1);
            }

            context.FillRectangle(accent, tag, 3);
            context.DrawText(text, new Point(tag.X + 5, tag.Y + 2));
        }
    }

    private void DrawHint(DrawingContext context, Rect picture)
    {
        var text = new FormattedText(OcrTexts.DrawHint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font("Oadm.FontFamily"),
            Number("Oadm.FontSizeBody", 12), Brush("Oadm.TextPrimaryBrush"));
        var box = new Rect(picture.X + ((picture.Width - text.Width) / 2) - 10, picture.Y + 10, text.Width + 20, text.Height + 10);
        context.FillRectangle(Brush("Oadm.OverlayBrush"), box, 4);
        context.DrawText(text, new Point(box.X + 10, box.Y + 5));
    }

    internal static Rect Fit(Size space, double aspect)
    {
        if (space.Width <= 0 || space.Height <= 0)
        {
            return default;
        }

        var width = space.Width;
        var height = width / aspect;
        if (height > space.Height)
        {
            height = space.Height;
            width = height * aspect;
        }

        return new Rect((space.Width - width) / 2, (space.Height - height) / 2, width, height);
    }

    private static Rect ToRect(RelativeBox box, Rect picture) =>
        new(picture.X + (box.X * picture.Width), picture.Y + (box.Y * picture.Height), box.Width * picture.Width, box.Height * picture.Height);

    private static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private object? Find(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value : null;

    private IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;

    private double Number(string key, double fallback) => Find(key) is double value ? value : fallback;

    private Typeface Font(string key) => new(Find(key) as FontFamily ?? FontFamily.Default);
}
