using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>The end of a move or resize of an area on the picture: the area and its new box.</summary>
public sealed record AreaBoxChange(AreaViewModel Area, RelativeBox Box);

/// <summary>
/// The overlay over the live video of the shown reader (docs/spec/ocr.md "Page"): the reader's areas dashed in the accent
/// colour with their name on a tag, the selected one solid with handles at its corners and edge midpoints; the lines of the
/// latest reading as teal boxes with their text. The picture's place inside the control follows the reader's resolution
/// (its shape kept, centered: the same letterboxing as the video below it). While <see cref="IsDrawing"/> a drag draws a
/// rectangle (<see cref="AreaDrawn"/>); otherwise a press on an area selects it (<see cref="AreaSelected"/>), a drag inside
/// moves it, a drag on a handle resizes it (<see cref="AreaChanged"/> at the end; Esc cancels the drag), the arrow keys
/// nudge it (<see cref="NudgeRequested"/>). While an area is drawn, moved or resized the lines are hidden
/// (<see cref="AdjustingChanged"/> tells the reader); without areas (<see cref="ShowStartHint"/>) the picture is dimmed with
/// "Start by drawing an area". While the page is paused <see cref="HeldPicture"/> (the frame the server read) is drawn under
/// them on the same rectangle, so areas and lines fit it exactly. The geometry rules are <see cref="AreaGeometry"/>; colours
/// and fonts come from the theme.
/// </summary>
public sealed class ReadingPicture : Control
{
    public static readonly StyledProperty<Resolution> PictureSizeProperty =
        AvaloniaProperty.Register<ReadingPicture, Resolution>(nameof(PictureSize), Resolution.Default);

    public static readonly StyledProperty<IReadOnlyList<AreaViewModel>?> AreasProperty =
        AvaloniaProperty.Register<ReadingPicture, IReadOnlyList<AreaViewModel>?>(nameof(Areas));

    public static readonly StyledProperty<IReadOnlyList<TextLine>?> LinesProperty =
        AvaloniaProperty.Register<ReadingPicture, IReadOnlyList<TextLine>?>(nameof(Lines));

    public static readonly StyledProperty<bool> IsDrawingProperty =
        AvaloniaProperty.Register<ReadingPicture, bool>(nameof(IsDrawing));

    /// <summary>Areas can be moved and resized (the change right).</summary>
    public static readonly StyledProperty<bool> CanEditProperty =
        AvaloniaProperty.Register<ReadingPicture, bool>(nameof(CanEdit), true);

    /// <summary>The reader has no area: the picture is dimmed with <see cref="OcrTexts.StartHint"/>.</summary>
    public static readonly StyledProperty<bool> ShowStartHintProperty =
        AvaloniaProperty.Register<ReadingPicture, bool>(nameof(ShowStartHint));

    /// <summary>The frame the server holds while paused, drawn in the picture's place; null: the live video shows through.</summary>
    public static readonly StyledProperty<IImage?> HeldPictureProperty =
        AvaloniaProperty.Register<ReadingPicture, IImage?>(nameof(HeldPicture));

    /// <summary>Changes whenever the areas, the selection or the lines changed: the overlay draws again.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<ReadingPicture, int>(nameof(Version));

    /// <summary>The smallest area drawn, relative to the picture (a click is not an area).</summary>
    public const double MinAreaSize = AreaGeometry.MinSize;

    /// <summary>An area with a side below this (pixels) is small: the handles' reach would cover it, a press inside moves it.</summary>
    internal const double SmallArea = ShapeHandles.SmallArea;

    private Point? _drawStart;
    private Point _drawNow;
    private EditDrag? _edit;

    static ReadingPicture()
    {
        AffectsRender<ReadingPicture>(AreasProperty, LinesProperty, IsDrawingProperty, VersionProperty, ShowStartHintProperty, HeldPictureProperty);
        AffectsMeasure<ReadingPicture>(PictureSizeProperty);
        FocusableProperty.OverrideDefaultValue<ReadingPicture>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ReadingPicture>(true);
    }

    /// <summary>A rectangle was drawn, relative to the picture.</summary>
    public event EventHandler<RelativeBox>? AreaDrawn;

    /// <summary>A press on an area (not drawing): it becomes the selected one.</summary>
    public event EventHandler<AreaViewModel>? AreaSelected;

    /// <summary>A press beside every area: no area is selected any more.</summary>
    public event EventHandler? SelectionCleared;

    /// <summary>A move or resize ended with another box.</summary>
    public event EventHandler<AreaBoxChange>? AreaChanged;

    /// <summary>An arrow key (not drawing, no drag): move the selected area by this much, relative to the picture.</summary>
    public event EventHandler<Vector>? NudgeRequested;

    /// <summary>Esc while drawing.</summary>
    public event EventHandler? DrawingCancelled;

    /// <summary>A move or resize started (true) or ended or was cancelled (false): the reader hides the lines meanwhile.</summary>
    public event EventHandler<bool>? AdjustingChanged;

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

    public bool CanEdit
    {
        get => GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }

    public bool ShowStartHint
    {
        get => GetValue(ShowStartHintProperty);
        set => SetValue(ShowStartHintProperty, value);
    }

    public IImage? HeldPicture
    {
        get => GetValue(HeldPictureProperty);
        set => SetValue(HeldPictureProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>Where the picture sits inside the control (its shape kept, centered).</summary>
    public Rect PictureRect => PictureFit.Fit(Bounds.Size, Aspect);

    /// <summary>A move or resize is in progress.</summary>
    public bool IsDragging => _edit is not null;

    private double Aspect => PictureFit.AspectOf(PictureSize.Width, PictureSize.Height);

    /// <summary>A point of the control relative to the picture (0 to 1 each, clamped).</summary>
    public Point ToRelative(Point point) => PictureFit.ToRelative(point, PictureRect);

    /// <summary>The rectangle between two relative points.</summary>
    public static RelativeBox BoxBetween(Point a, Point b) => AreaGeometry.Between(a.X, a.Y, b.X, b.Y);

    /// <summary>Finishes a drawing drag from <paramref name="start"/> to <paramref name="end"/> (relative): an area or nothing.</summary>
    public void FinishDrag(Point start, Point end)
    {
        var box = BoxBetween(start, end);
        if (AreaGeometry.IsBigEnough(box))
        {
            AreaDrawn?.Invoke(this, box);
        }
    }

    /// <summary>
    /// What a press at <paramref name="point"/> (in the control) takes: a handle of the selected area, else the topmost
    /// area under it (move), else nothing. On a small area (a side below <see cref="SmallArea"/>) a press inside always moves
    /// it and its handles are taken just outside it, so the handles' reach never covers the whole area.
    /// </summary>
    public (AreaViewModel? Area, AreaHandle Handle) HitTest(Point point)
    {
        var picture = PictureRect;
        if (Areas is not { Count: > 0 } areas || picture.Width <= 0)
        {
            return (null, AreaHandle.None);
        }

        if (areas.FirstOrDefault(a => a.IsSelected) is { } selected
            && ToRect(selected.Box, picture) is var rect
            && !(rect.Contains(point) && (rect.Width < SmallArea || rect.Height < SmallArea)))
        {
            if (ShapeHandles.HitRect(rect, point) is var handle and not AreaHandle.None)
            {
                return (selected, handle);
            }
        }

        for (var i = areas.Count - 1; i >= 0; i--)
        {
            if (ToRect(areas[i].Box, picture).Contains(point))
            {
                return (areas[i], AreaHandle.Move);
            }
        }

        return (null, AreaHandle.None);
    }

    /// <summary>Starts a move or resize of <paramref name="area"/> at the relative point <paramref name="start"/> (tests and the pointer).</summary>
    internal void BeginEdit(AreaViewModel area, AreaHandle handle, Point start)
    {
        _edit = new EditDrag(area, handle, start, area.Box) { Now = area.Box };
        AdjustingChanged?.Invoke(this, true);
        InvalidateVisual();
    }

    /// <summary>The drag reached the relative point <paramref name="now"/>: the area is shown at its new box.</summary>
    internal void UpdateEdit(Point now)
    {
        if (_edit is { } edit)
        {
            edit.Now = AreaGeometry.Resize(edit.StartBox, edit.Handle, now.X - edit.Start.X, now.Y - edit.Start.Y);
            InvalidateVisual();
        }
    }

    /// <summary>The drag ended: <see cref="AreaChanged"/> when the box changed.</summary>
    internal void EndEdit()
    {
        if (_edit is not { } edit)
        {
            return;
        }

        _edit = null;
        if (edit.Now != edit.StartBox)
        {
            AreaChanged?.Invoke(this, new AreaBoxChange(edit.Area, edit.Now));
        }

        AdjustingChanged?.Invoke(this, false);
        InvalidateVisual();
    }

    /// <summary>Esc during a drag, or the pointer lost: the area stays where it was.</summary>
    internal void CancelEdit()
    {
        if (_edit is null)
        {
            return;
        }

        _edit = null;
        AdjustingChanged?.Invoke(this, false);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => PictureFit.Measure(availableSize, Aspect);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        var relative = ToRelative(position);
        if (IsDrawing)
        {
            _drawStart = relative;
            _drawNow = relative;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var (area, handle) = HitTest(position);
        if (area is null)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
            return;
        }

        AreaSelected?.Invoke(this, area);
        if (CanEdit)
        {
            BeginEdit(area, handle, relative);
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_drawStart is not null)
        {
            _drawNow = ToRelative(position);
            InvalidateVisual();
            return;
        }

        if (_edit is not null)
        {
            UpdateEdit(ToRelative(position));
            return;
        }

        Cursor = IsDrawing ? new Cursor(StandardCursorType.Cross) : ShapeHandles.CursorOf(CanEdit ? HitTest(position).Handle : AreaHandle.None);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_drawStart is { } start)
        {
            _drawStart = null;
            e.Pointer.Capture(null);
            FinishDrag(start, ToRelative(e.GetPosition(this)));
            InvalidateVisual();
            return;
        }

        if (_edit is not null)
        {
            // End the drag first, then let go of the pointer: releasing the capture raises PointerCaptureLost at once, which
            // cancels a drag still in progress (the area snapped back to where it was).
            UpdateEdit(ToRelative(e.GetPosition(this)));
            EndEdit();
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drawStart = null;
        if (_edit is not null)
        {
            CancelEdit();
        }

        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _edit is not null)
        {
            CancelEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && IsDrawing)
        {
            _drawStart = null;
            DrawingCancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (IsDrawing || _edit is not null || !CanEdit)
        {
            return;
        }

        if (ShapeGeometry.NudgeOf(e.Key, e.KeyModifiers) is { } by && Areas?.Any(a => a.IsSelected) == true)
        {
            NudgeRequested?.Invoke(this, by);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsDrawingProperty)
        {
            _drawStart = null;
            Cursor = IsDrawing ? new Cursor(StandardCursorType.Cross) : null;
        }
    }

    /// <summary>Shows a drawing drag in progress (tests: the drawing screenshot without a pointer).</summary>
    internal void ShowDrag(Point start, Point now)
    {
        _drawStart = start;
        _drawNow = now;
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

        // A transparent fill: the pointer reaches the overlay over the whole picture.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        using (context.PushClip(picture))
        {
            if (HeldPicture is { } held)
            {
                context.DrawImage(held, new Rect(held.Size), picture);
            }

            if (ShowStartHint && !IsDrawing)
            {
                // No area yet: nothing is read; the picture dimmed a little.
                context.FillRectangle(Tint(Brush("Oadm.VideoDimBrush"), StartDim), picture);
            }

            // The lines of the latest reading, never while an area is drawn, moved or resized.
            if (!IsDrawing && _edit is null && _drawStart is null)
            {
                DrawLines(context, picture);
            }

            DrawAreas(context, picture);
            if (_drawStart is { } start)
            {
                var accent = Brush("Oadm.AccentBrush");
                var rect = ToRect(BoxBetween(start, _drawNow), picture);
                context.DrawRectangle(Tint(accent, 0.16), new Pen(accent, 2), rect);
            }
        }

        if (IsDrawing)
        {
            ShapePainter.DrawHint(context, new OverlayTheme(this), picture, OcrTexts.DrawHint, top: true);
        }
        else if (ShowStartHint)
        {
            ShapePainter.DrawHint(context, new OverlayTheme(this), picture, OcrTexts.StartHint, top: false);
        }
    }

    /// <summary>The share of the theme's video dim over a picture without areas (slightly darker, the picture still shows).</summary>
    private const double StartDim = 0.6;

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
        var grey = Brush("Oadm.TextSecondaryBrush");
        var dashed = new Pen(grey, 2, new DashStyle([4, 3], 0));
        var solid = new Pen(accent, 2);
        var greyTag = Brush("Oadm.OverlayBrush");
        var greyText = Brush("Oadm.TextPrimaryBrush");
        var font = Font("Oadm.FontFamily");
        var size = Number("Oadm.FontSizeSmall", 11);
        foreach (var area in areas)
        {
            var box = _edit is { } edit && ReferenceEquals(edit.Area, area) ? edit.Now : area.Box;
            var rect = ToRect(box, picture);
            // Selected: the accent, solid. The others stay grey, so the one being worked on stands out.
            context.DrawRectangle(area.IsSelected ? Tint(accent, 0.16) : Tint(grey, 0.06), area.IsSelected ? solid : dashed, rect);
            var name = string.IsNullOrWhiteSpace(area.Name) ? " " : area.Name.Trim();
            var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, area.IsSelected ? onAccent : greyText);
            ShapePainter.DrawTag(context, text, rect.TopLeft, picture, area.IsSelected ? accent : greyTag);
            if (area.IsSelected && CanEdit && !IsDrawing)
            {
                ShapePainter.DrawHandles(context, ShapeHandles.OfRect(rect).Select(h => h.At), accent, onAccent);
            }
        }
    }

    private static Rect ToRect(RelativeBox box, Rect picture) => PictureFit.ToControl(box.X, box.Y, box.Width, box.Height, picture);

    private static IBrush Tint(IBrush brush, double opacity) => OverlayTheme.Tint(brush, opacity);

    private IBrush Brush(string key) => new OverlayTheme(this).Brush(key);

    private double Number(string key, double fallback) => new OverlayTheme(this).Number(key, fallback);

    private Typeface Font(string key) => new OverlayTheme(this).Font(key);

    /// <summary>A move or resize in progress: the area, what moves, where it started (relative) and the box now.</summary>
    private sealed class EditDrag(AreaViewModel area, AreaHandle handle, Point start, RelativeBox startBox)
    {
        public AreaViewModel Area { get; } = area;

        public AreaHandle Handle { get; } = handle;

        public Point Start { get; } = start;

        public RelativeBox StartBox { get; } = startBox;

        public RelativeBox Now { get; set; }
    }
}
