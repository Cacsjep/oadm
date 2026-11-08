using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>The end of a drag on a shape: the shape and its new points.</summary>
public sealed record ShapeChange(ShapeItem Shape, IReadOnlyList<Point> Points);

/// <summary>
/// The overlay over the live video of the shown pipeline (docs/spec/vision-center.md "Page"): the outputs of the nodes whose
/// eye is on and of the selected node (<see cref="VideoOverlay"/>), each in its node's colour, and the Area and Line shapes,
/// edited like the areas of Text recognition with the shared shape rules (plugins/Shared/Vision.Client): a press on a shape
/// selects it, a drag inside moves it, a drag on a point moves that point, a double-click on an edge adds a point and on a
/// point removes it; Esc cancels a drag, the arrow keys nudge the selected shape. While <see cref="DrawKind"/> is set a drag
/// draws the shape (a rectangle's corners for an Area, the two ends for a Line). While paused <see cref="HeldPicture"/> is
/// drawn in the picture's place. Colours and fonts come from the theme.
/// </summary>
public sealed class VisionPicture : Control
{
    public static readonly StyledProperty<Resolution> PictureSizeProperty =
        AvaloniaProperty.Register<VisionPicture, Resolution>(nameof(PictureSize), Resolution.Default);

    public static readonly StyledProperty<IReadOnlyList<OverlayItem>?> ItemsProperty =
        AvaloniaProperty.Register<VisionPicture, IReadOnlyList<OverlayItem>?>(nameof(Items));

    public static readonly StyledProperty<IReadOnlyList<ShapeItem>?> ShapesProperty =
        AvaloniaProperty.Register<VisionPicture, IReadOnlyList<ShapeItem>?>(nameof(Shapes));

    /// <summary>A shape is being drawn: Area (closed) or Line; null while not drawing.</summary>
    public static readonly StyledProperty<string?> DrawKindProperty =
        AvaloniaProperty.Register<VisionPicture, string?>(nameof(DrawKind));

    public static readonly StyledProperty<bool> CanEditProperty =
        AvaloniaProperty.Register<VisionPicture, bool>(nameof(CanEdit), true);

    public static readonly StyledProperty<IImage?> HeldPictureProperty =
        AvaloniaProperty.Register<VisionPicture, IImage?>(nameof(HeldPicture));

    /// <summary>The smallest drawn shape, relative to the picture (a click is not a shape).</summary>
    public const double MinDrawn = 0.02;

    private Point? _drawStart;
    private Point _drawNow;
    private ShapeDrag? _drag;

    static VisionPicture()
    {
        AffectsRender<VisionPicture>(ItemsProperty, ShapesProperty, DrawKindProperty, HeldPictureProperty, CanEditProperty);
        AffectsMeasure<VisionPicture>(PictureSizeProperty);
        FocusableProperty.OverrideDefaultValue<VisionPicture>(true);
        ClipToBoundsProperty.OverrideDefaultValue<VisionPicture>(true);
    }

    public event EventHandler<IReadOnlyList<Point>>? ShapeDrawn;

    public event EventHandler<ShapeItem>? ShapeSelected;

    public event EventHandler<ShapeChange>? ShapeChanged;

    public event EventHandler<Vector>? NudgeRequested;

    public event EventHandler? DrawingCancelled;

    public Resolution PictureSize
    {
        get => GetValue(PictureSizeProperty);
        set => SetValue(PictureSizeProperty, value);
    }

    public IReadOnlyList<OverlayItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public IReadOnlyList<ShapeItem>? Shapes
    {
        get => GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    public string? DrawKind
    {
        get => GetValue(DrawKindProperty);
        set => SetValue(DrawKindProperty, value);
    }

    public bool CanEdit
    {
        get => GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }

    public IImage? HeldPicture
    {
        get => GetValue(HeldPictureProperty);
        set => SetValue(HeldPictureProperty, value);
    }

    public bool IsDrawing => DrawKind is not null;

    public bool IsDragging => _drag is not null;

    public Rect PictureRect => PictureFit.Fit(Bounds.Size, Aspect);

    private double Aspect => PictureFit.AspectOf(PictureSize?.Width ?? 0, PictureSize?.Height ?? 0);

    public Point ToRelative(Point point) => PictureFit.ToRelative(point, PictureRect);

    /// <summary>What a press at <paramref name="point"/> (in the control) takes: a point of the selected shape, else the topmost shape (move).</summary>
    public (ShapeItem? Shape, ShapeHit Hit) HitTest(Point point)
    {
        var picture = PictureRect;
        if (Shapes is not { Count: > 0 } shapes || picture.Width <= 0)
        {
            return (null, ShapeHit.None);
        }

        if (shapes.FirstOrDefault(s => s.IsSelected) is { } selected
            && ShapeHandles.HitVertex([.. selected.Points.Select(p => PictureFit.ToControl(p, picture))], point) is var vertex and >= 0)
        {
            return (selected, new ShapeHit(AreaHandle.Vertex, vertex));
        }

        for (var i = shapes.Count - 1; i >= 0; i--)
        {
            var pixels = shapes[i].Points.Select(p => PictureFit.ToControl(p, picture)).ToList();
            var inside = shapes[i].Closed ? ShapeGeometry.Inside(pixels, point) : ShapeHandles.HitEdge(pixels, point, closed: false) >= 0;
            if (inside || ShapeHandles.HitEdge(pixels, point, shapes[i].Closed) >= 0)
            {
                return (shapes[i], new ShapeHit(AreaHandle.Move));
            }
        }

        return (null, ShapeHit.None);
    }

    /// <summary>Starts a drag of <paramref name="shape"/> at the relative <paramref name="start"/> (tests and the pointer).</summary>
    internal void BeginDrag(ShapeItem shape, ShapeHit hit, Point start)
    {
        _drag = new ShapeDrag(shape, hit, start) { Now = shape.Points };
        InvalidateVisual();
    }

    internal void UpdateDrag(Point now)
    {
        if (_drag is { } drag)
        {
            var dx = now.X - drag.Start.X;
            var dy = now.Y - drag.Start.Y;
            drag.Now = drag.Hit.Handle == AreaHandle.Vertex
                ? ShapeGeometry.MoveVertex(drag.Shape.Points, drag.Hit.Vertex, dx, dy)
                : ShapeGeometry.MovePoints(drag.Shape.Points, dx, dy);
            InvalidateVisual();
        }
    }

    internal void EndDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (!drag.Now.SequenceEqual(drag.Shape.Points))
        {
            ShapeChanged?.Invoke(this, new ShapeChange(drag.Shape, drag.Now));
        }

        InvalidateVisual();
    }

    internal void CancelDrag()
    {
        _drag = null;
        InvalidateVisual();
    }

    /// <summary>A drawing drag from <paramref name="start"/> to <paramref name="end"/> (relative): the shape's points, or nothing for a click.</summary>
    public void FinishDraw(Point start, Point end)
    {
        if (DrawKind == NodeKinds.Line)
        {
            if (CanvasGeometry.Distance(start, end) >= MinDrawn)
            {
                ShapeDrawn?.Invoke(this, [start, end]);
            }

            return;
        }

        var box = ShapeGeometry.Between(start.X, start.Y, end.X, end.Y);
        if (ShapeGeometry.IsBigEnough(box, MinDrawn))
        {
            ShapeDrawn?.Invoke(this, ShapeGeometry.Corners(box));
        }
    }

    /// <summary>Shows a drawing drag in progress (tests: the drawing screenshot without a pointer).</summary>
    internal void ShowDraw(Point start, Point now)
    {
        _drawStart = start;
        _drawNow = now;
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

        var (shape, hit) = HitTest(position);
        if (shape is null)
        {
            return;
        }

        if (e.ClickCount == 2 && CanEdit && shape.IsSelected)
        {
            EditPoints(shape, position, hit);
            e.Handled = true;
            return;
        }

        ShapeSelected?.Invoke(this, shape);
        if (CanEdit)
        {
            // The selection made a new shape list: drag the shape as it is now.
            var now = Shapes?.FirstOrDefault(s => ReferenceEquals(s.Node, shape.Node)) ?? shape;
            BeginDrag(now, hit.Handle == AreaHandle.Vertex && !now.IsSelected ? new ShapeHit(AreaHandle.Move) : hit, relative);
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    /// <summary>A double-click on the selected shape: on a point it goes (not below the fewest), on an edge a point is added.</summary>
    private void EditPoints(ShapeItem shape, Point position, ShapeHit hit)
    {
        var picture = PictureRect;
        IReadOnlyList<Point> points;
        if (hit.Handle == AreaHandle.Vertex)
        {
            points = ShapeGeometry.RemoveVertex(shape.Points, hit.Vertex, shape.MinPoints);
        }
        else
        {
            var pixels = shape.Points.Select(p => PictureFit.ToControl(p, picture)).ToList();
            var edge = ShapeHandles.HitEdge(pixels, position, shape.Closed);
            points = edge < 0 ? shape.Points : ShapeGeometry.InsertVertex(shape.Points, edge, ToRelative(position), shape.MaxPoints);
        }

        if (!points.SequenceEqual(shape.Points))
        {
            ShapeChanged?.Invoke(this, new ShapeChange(shape, points));
        }
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

        if (_drag is not null)
        {
            UpdateDrag(ToRelative(position));
            return;
        }

        Cursor = IsDrawing ? new Cursor(StandardCursorType.Cross) : ShapeHandles.CursorOf(CanEdit ? HitTest(position).Hit.Handle : AreaHandle.None);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_drawStart is { } start)
        {
            _drawStart = null;
            e.Pointer.Capture(null);
            FinishDraw(start, ToRelative(e.GetPosition(this)));
            InvalidateVisual();
            return;
        }

        if (_drag is not null)
        {
            // End the drag first, then let go of the pointer (releasing the capture cancels a drag still in progress).
            UpdateDrag(ToRelative(e.GetPosition(this)));
            EndDrag();
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drawStart = null;
        CancelDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag is not null)
        {
            CancelDrag();
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

        if (IsDrawing || _drag is not null || !CanEdit)
        {
            return;
        }

        if (ShapeGeometry.NudgeOf(e.Key, e.KeyModifiers) is { } by && Shapes?.Any(s => s.IsSelected) == true)
        {
            NudgeRequested?.Invoke(this, by);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == DrawKindProperty)
        {
            _drawStart = null;
            Cursor = IsDrawing ? new Cursor(StandardCursorType.Cross) : null;
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var picture = PictureRect;
        if (picture.Width <= 0 || picture.Height <= 0)
        {
            return;
        }

        var theme = new OverlayTheme(this);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        using (context.PushClip(picture))
        {
            if (HeldPicture is { } held)
            {
                context.DrawImage(held, new Rect(held.Size), picture);
            }

            if (!IsDrawing && _drag is null)
            {
                DrawItems(context, theme, picture);
            }

            DrawShapes(context, theme, picture);
            if (_drawStart is { } start)
            {
                var accent = theme.Brush("Oadm.AccentBrush");
                if (DrawKind == NodeKinds.Line)
                {
                    context.DrawLine(new Pen(accent, 3), PictureFit.ToControl(start, picture), PictureFit.ToControl(_drawNow, picture));
                }
                else
                {
                    var box = ShapeGeometry.Between(start.X, start.Y, _drawNow.X, _drawNow.Y);
                    context.DrawRectangle(OverlayTheme.Tint(accent, 0.16), new Pen(accent, 2), PictureFit.ToControl(box.X, box.Y, box.Width, box.Height, picture));
                }
            }
        }

        if (IsDrawing)
        {
            ShapePainter.DrawHint(context, theme, picture, DrawKind == NodeKinds.Line ? VisionTexts.DrawLineHint : VisionTexts.DrawAreaHint, top: true);
        }
    }

    private void DrawItems(DrawingContext context, OverlayTheme theme, Rect picture)
    {
        if (Items is not { Count: > 0 } items)
        {
            return;
        }

        var onColour = theme.Brush("Oadm.TextOnLightBrush");
        var badges = 0;
        var taken = ShapeTags(theme, picture);
        foreach (var item in items)
        {
            var brush = theme.Brush(item.BrushKey);
            switch (item.Kind)
            {
                case OverlayItemKind.Trail:
                    context.DrawGeometry(null, new Pen(OverlayTheme.Tint(brush, 0.7), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                        ShapePainter.Outline([.. item.Points.Select(p => PictureFit.ToControl(p, picture))], closed: false));
                    break;
                case OverlayItemKind.Box when item.Points.Count == 2:
                    var rect = new Rect(PictureFit.ToControl(item.Points[0], picture), PictureFit.ToControl(item.Points[1], picture));
                    var pen = item.Dashed ? new Pen(OverlayTheme.Tint(brush, 0.6), 1.5, new DashStyle([4, 3], 0)) : new Pen(brush, 2);
                    context.DrawRectangle(item.Filled ? OverlayTheme.Tint(brush, 0.2) : null, pen, rect);
                    if (PlaceTag(theme, item, rect, picture, onColour, taken) is { } placed)
                    {
                        ShapePainter.DrawTagAt(context, placed.Text, placed.At, brush);
                        taken.Add(placed.At);
                    }

                    break;
                case OverlayItemKind.Badge:
                    var name = theme.Small(item.Label ?? string.Empty, theme.Brush("Oadm.TextPrimaryBrush"));
                    var value = theme.Body(item.Value ?? string.Empty, brush, FontWeight.SemiBold);
                    var width = Math.Max(name.Width, value.Width) + 20;
                    var badge = new Rect(picture.Right - width - 10, picture.Y + 10 + (badges++ * (name.Height + value.Height + 18)), width, name.Height + value.Height + 12);
                    context.FillRectangle(theme.Brush("Oadm.OverlayBrush"), badge, 4);
                    taken.Add(badge);
                    context.DrawText(name, new Point(badge.X + 10, badge.Y + 5));
                    context.DrawText(value, new Point(badge.X + 10, badge.Y + 7 + name.Height));
                    break;
            }
        }
    }

    /// <summary>
    /// Where a box's label goes without covering another label: above the box, else inside its top edge; with the short label
    /// (the score or the id alone) when the whole one does not fit; nowhere when neither fits.
    /// </summary>
    private static (FormattedText Text, Rect At)? PlaceTag(OverlayTheme theme, OverlayItem item, Rect box, Rect picture, IBrush brush, List<Rect> taken)
    {
        var texts = new Dictionary<string, FormattedText>(StringComparer.Ordinal);
        Size Measure(string label)
        {
            var text = theme.Small(label, brush);
            texts[label] = text;
            return new Size(text.Width, text.Height);
        }

        var place = VideoOverlay.PlaceLabel(item, box, picture, Measure, taken);
        return place is { } p ? (texts[p.Label], p.At) : null;
    }

    /// <summary>The tags of the shapes (drawn last, never moved): the box labels keep clear of them.</summary>
    private List<Rect> ShapeTags(OverlayTheme theme, Rect picture)
    {
        var tags = new List<Rect>();
        foreach (var shape in Shapes ?? [])
        {
            if (shape.Points.Count > 0)
            {
                tags.Add(ShapePainter.TagAbove(theme.Small(shape.Label, Brushes.Black), PictureFit.ToControl(shape.Points[0], picture), picture));
            }
        }

        return tags;
    }

    private void DrawShapes(DrawingContext context, OverlayTheme theme, Rect picture)
    {
        if (Shapes is not { Count: > 0 } shapes)
        {
            return;
        }

        var onColour = theme.Brush("Oadm.TextOnLightBrush");
        var white = theme.Brush("Oadm.TextOnAccentBrush");
        foreach (var shape in shapes)
        {
            var points = _drag is { } drag && ReferenceEquals(drag.Shape.Node, shape.Node) ? drag.Now : shape.Points;
            var pixels = points.Select(p => PictureFit.ToControl(p, picture)).ToList();
            if (pixels.Count == 0)
            {
                continue;
            }

            var brush = theme.Brush(shape.BrushKey);
            var pen = shape.IsSelected ? new Pen(brush, 2.5) : new Pen(brush, 2, new DashStyle([5, 3], 0));
            context.DrawGeometry(shape.Closed ? OverlayTheme.Tint(brush, 0.1) : null, pen, ShapePainter.Outline(pixels, shape.Closed));
            if (!shape.Closed && pixels.Count >= 2)
            {
                DrawInArrow(context, brush, VideoOverlay.InArrow(pixels));
            }

            ShapePainter.DrawTag(context, theme.Small(shape.Label, onColour), pixels[0], picture, brush);
            if (shape.IsSelected && CanEdit && !IsDrawing)
            {
                ShapePainter.DrawHandles(context, pixels, brush, white);
            }
        }
    }

    /// <summary>The In arrow at the line's middle, pointing to the side a crossing counts as In.</summary>
    private static void DrawInArrow(DrawingContext context, IBrush brush, (Point Middle, Vector Direction) arrow)
    {
        var (middle, direction) = arrow;
        var tip = middle + (direction * 26);
        var back = middle + (direction * 18);
        var side = new Vector(-direction.Y, direction.X) * 6;
        context.DrawLine(new Pen(brush, 2.5), middle + (direction * 4), back);
        var head = ShapePainter.Outline([tip, back + side, back - side], closed: true);
        context.DrawGeometry(brush, null, head);
    }

    private sealed class ShapeDrag(ShapeItem shape, ShapeHit hit, Point start)
    {
        public ShapeItem Shape { get; } = shape;

        public ShapeHit Hit { get; } = hit;

        public Point Start { get; } = start;

        public IReadOnlyList<Point> Now { get; set; } = [];
    }
}
