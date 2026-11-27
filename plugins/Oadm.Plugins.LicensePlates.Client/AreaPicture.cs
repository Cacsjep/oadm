using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>An area of ALPV on the picture: its corners relative to the picture (0 to 1) and its driving direction.</summary>
/// <param name="Id">1 or 2.</param>
/// <param name="Points">3 to 8 corners, relative to the picture.</param>
/// <param name="Angle">The driving direction that counts as In, in degrees clockwise from up (0 to 360).</param>
/// <param name="IsSelected">The selected area shows its handles.</param>
public sealed record AreaShape(int Id, IReadOnlyList<Point> Points, double Angle, bool IsSelected)
{
    public const int MinPoints = 3;
    public const int MaxPoints = 8;

    public string Label => Id == 1 ? PageTexts.Area1 : PageTexts.Area2;

    /// <summary>The theme brush of the area: the accent for area 1, teal for area 2.</summary>
    public string BrushKey => Id == 1 ? "Oadm.AccentBrush" : "Oadm.Tag.TealBrush";

    /// <summary>The middle of the corners (where the direction arrow starts).</summary>
    public Point Center => Points.Count == 0 ? new Point(0.5, 0.5) : new Point(Points.Average(p => p.X), Points.Average(p => p.Y));
}

/// <summary>The end of a drag on an area: its new corners, or its new direction.</summary>
public sealed record AreaChange(int Id, IReadOnlyList<Point>? Points, double? Angle);

/// <summary>
/// The overlay over the live video of the Set up dialog (docs/spec/license-plate-center.md "Set up"): the two areas of ALPV as
/// polygons of 3 to 8 corners, each with an arrow for the driving direction that counts as In. Edited like the areas of
/// Vision Center with the shared shape rules (plugins/Shared/Vision.Client): a press selects an area, a drag inside moves it,
/// a drag on a corner moves that corner, a double-click on an edge adds a corner and on a corner removes it (never below 3 or
/// above 8), a drag on the arrow's head turns it; Esc cancels a drag, the arrow keys nudge the selected area. Colours and fonts
/// come from the theme; the picture keeps its shape (<see cref="PictureFit"/>), so the areas fit the video below.
/// </summary>
public sealed class AreaPicture : Control
{
    public static readonly StyledProperty<IReadOnlyList<AreaShape>?> AreasProperty =
        AvaloniaProperty.Register<AreaPicture, IReadOnlyList<AreaShape>?>(nameof(Areas));

    public static readonly StyledProperty<bool> CanEditProperty =
        AvaloniaProperty.Register<AreaPicture, bool>(nameof(CanEdit), true);

    /// <summary>The picture's width divided by its height (16:9 when the stream size is not known).</summary>
    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<AreaPicture, double>(nameof(Aspect), 16.0 / 9);

    /// <summary>The direction arrow's length, relative to the picture's shorter side.</summary>
    public const double ArrowLength = 0.16;

    private Drag? _drag;

    static AreaPicture()
    {
        AffectsRender<AreaPicture>(AreasProperty, CanEditProperty);
        AffectsMeasure<AreaPicture>(AspectProperty);
        FocusableProperty.OverrideDefaultValue<AreaPicture>(true);
        ClipToBoundsProperty.OverrideDefaultValue<AreaPicture>(true);
    }

    public event EventHandler<int>? AreaSelected;

    public event EventHandler<AreaChange>? AreaChanged;

    public event EventHandler<Vector>? NudgeRequested;

    public IReadOnlyList<AreaShape>? Areas
    {
        get => GetValue(AreasProperty);
        set => SetValue(AreasProperty, value);
    }

    public bool CanEdit
    {
        get => GetValue(CanEditProperty);
        set => SetValue(CanEditProperty, value);
    }

    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public Rect PictureRect => PictureFit.Fit(Bounds.Size, Aspect);

    /// <summary>The head of an area's arrow, relative to the picture.</summary>
    public static Point ArrowHead(AreaShape area, Rect picture)
    {
        ArgumentNullException.ThrowIfNull(area);
        var center = PictureFit.ToControl(area.Center, picture);
        var length = ArrowLength * Math.Min(picture.Width, picture.Height);
        var radians = area.Angle * Math.PI / 180;
        return new Point(center.X + (Math.Sin(radians) * length), center.Y - (Math.Cos(radians) * length));
    }

    /// <summary>The angle (degrees clockwise from up) of the direction from an area's middle to <paramref name="point"/> (in the control).</summary>
    public static double AngleTo(Point center, Point point)
    {
        var degrees = Math.Atan2(point.X - center.X, center.Y - point.Y) * 180 / Math.PI;
        return (degrees + 360) % 360;
    }

    /// <summary>What a press at <paramref name="point"/> (in the control) takes: the arrow's head or a corner of the selected area, else the topmost area.</summary>
    public (AreaShape? Area, DragKind Kind, int Vertex) HitTest(Point point)
    {
        var picture = PictureRect;
        if (Areas is not { Count: > 0 } areas || picture.Width <= 0)
        {
            return (null, DragKind.None, -1);
        }

        foreach (var area in areas.OrderByDescending(a => a.IsSelected))
        {
            if (ShapeHandles.Near(point, ArrowHead(area, picture)))
            {
                return (area, DragKind.Arrow, -1);
            }
        }

        if (areas.FirstOrDefault(a => a.IsSelected) is { } selected
            && ShapeHandles.HitVertex([.. selected.Points.Select(p => PictureFit.ToControl(p, picture))], point) is var vertex and >= 0)
        {
            return (selected, DragKind.Vertex, vertex);
        }

        for (var i = areas.Count - 1; i >= 0; i--)
        {
            var pixels = areas[i].Points.Select(p => PictureFit.ToControl(p, picture)).ToList();
            if (ShapeGeometry.Inside(pixels, point) || ShapeHandles.HitEdge(pixels, point, closed: true) >= 0)
            {
                return (areas[i], DragKind.Move, -1);
            }
        }

        return (null, DragKind.None, -1);
    }

    /// <summary>Starts a drag (tests and the pointer); <paramref name="start"/> is relative to the picture.</summary>
    internal void BeginDrag(AreaShape area, DragKind kind, int vertex, Point start)
    {
        _drag = new Drag(area, kind, vertex, start) { Points = area.Points, Angle = area.Angle };
        InvalidateVisual();
    }

    internal void UpdateDrag(Point now)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var dx = now.X - drag.Start.X;
        var dy = now.Y - drag.Start.Y;
        switch (drag.Kind)
        {
            case DragKind.Vertex:
                drag.Points = ShapeGeometry.MoveVertex(drag.Area.Points, drag.Vertex, dx, dy);
                break;
            case DragKind.Move:
                drag.Points = ShapeGeometry.MovePoints(drag.Area.Points, dx, dy);
                break;
            case DragKind.Arrow:
                var picture = PictureRect;
                drag.Angle = Math.Round(AngleTo(PictureFit.ToControl(drag.Area.Center, picture), PictureFit.ToControl(now, picture)));
                break;
        }

        InvalidateVisual();
    }

    internal void EndDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (drag.Kind == DragKind.Arrow)
        {
            if (Math.Abs(drag.Angle - drag.Area.Angle) > 0.01)
            {
                AreaChanged?.Invoke(this, new AreaChange(drag.Area.Id, null, drag.Angle));
            }
        }
        else if (!drag.Points.SequenceEqual(drag.Area.Points))
        {
            AreaChanged?.Invoke(this, new AreaChange(drag.Area.Id, drag.Points, null));
        }

        InvalidateVisual();
    }

    internal void CancelDrag()
    {
        _drag = null;
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
        var (area, kind, vertex) = HitTest(position);
        if (area is null)
        {
            return;
        }

        if (e.ClickCount == 2 && CanEdit && area.IsSelected && kind != DragKind.Arrow)
        {
            EditCorners(area, position, kind, vertex);
            e.Handled = true;
            return;
        }

        AreaSelected?.Invoke(this, area.Id);
        if (CanEdit)
        {
            // The selection made a new list: drag the area as it is now.
            var now = Areas?.FirstOrDefault(a => a.Id == area.Id) ?? area;
            BeginDrag(now, kind == DragKind.Vertex && !area.IsSelected ? DragKind.Move : kind, vertex, PictureFit.ToRelative(position, PictureRect));
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    /// <summary>A double-click on the selected area: on a corner it goes (never below 3), on an edge a corner is added (at most 8).</summary>
    private void EditCorners(AreaShape area, Point position, DragKind kind, int vertex)
    {
        var picture = PictureRect;
        IReadOnlyList<Point> points;
        if (kind == DragKind.Vertex)
        {
            points = ShapeGeometry.RemoveVertex(area.Points, vertex, AreaShape.MinPoints);
        }
        else
        {
            var pixels = area.Points.Select(p => PictureFit.ToControl(p, picture)).ToList();
            var edge = ShapeHandles.HitEdge(pixels, position, closed: true);
            points = edge < 0 ? area.Points : ShapeGeometry.InsertVertex(area.Points, edge, PictureFit.ToRelative(position, picture), AreaShape.MaxPoints);
        }

        if (!points.SequenceEqual(area.Points))
        {
            AreaChanged?.Invoke(this, new AreaChange(area.Id, points, null));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_drag is not null)
        {
            UpdateDrag(PictureFit.ToRelative(position, PictureRect));
            return;
        }

        var kind = CanEdit ? HitTest(position).Kind : DragKind.None;
        Cursor = kind switch
        {
            DragKind.Arrow => new Cursor(StandardCursorType.Hand),
            DragKind.Vertex or DragKind.Move => new Cursor(StandardCursorType.SizeAll),
            _ => null,
        };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            // End the drag first, then let go of the pointer (releasing the capture cancels a drag still in progress).
            UpdateDrag(PictureFit.ToRelative(e.GetPosition(this), PictureRect));
            EndDrag();
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
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

        if (_drag is null && CanEdit && ShapeGeometry.NudgeOf(e.Key, e.KeyModifiers) is { } by && Areas?.Any(a => a.IsSelected) == true)
        {
            NudgeRequested?.Invoke(this, by);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var picture = PictureRect;
        if (picture.Width <= 0 || picture.Height <= 0 || Areas is not { Count: > 0 } areas)
        {
            return;
        }

        var theme = new OverlayTheme(this);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var onColour = theme.Brush("Oadm.TextOnLightBrush");
        var white = theme.Brush("Oadm.TextOnAccentBrush");
        using (context.PushClip(picture))
        {
            foreach (var area in areas.OrderBy(a => a.IsSelected))
            {
                var dragged = _drag is { } drag && drag.Area.Id == area.Id ? drag : null;
                var points = dragged is { Kind: not DragKind.Arrow } ? dragged.Points : area.Points;
                var angle = dragged is { Kind: DragKind.Arrow } ? dragged.Angle : area.Angle;
                var shown = area with { Points = points, Angle = angle };
                var pixels = points.Select(p => PictureFit.ToControl(p, picture)).ToList();
                if (pixels.Count == 0)
                {
                    continue;
                }

                var brush = theme.Brush(area.BrushKey);
                var pen = area.IsSelected ? new Pen(brush, 2.5) : new Pen(brush, 2, new DashStyle([5, 3], 0));
                context.DrawGeometry(OverlayTheme.Tint(brush, area.IsSelected ? 0.16 : 0.08), pen, ShapePainter.Outline(pixels, closed: true));
                DrawArrow(context, brush, white, PictureFit.ToControl(shown.Center, picture), ArrowHead(shown, picture));
                var top = pixels.OrderBy(p => p.Y).ThenBy(p => p.X).First();
                ShapePainter.DrawTag(context, theme.Small(area.Label, onColour), top, picture, brush);
                if (area.IsSelected && CanEdit)
                {
                    ShapePainter.DrawHandles(context, pixels, brush, white);
                }
            }
        }
    }

    /// <summary>The direction arrow: a line from the area's middle, a filled head and a round grip at its tip.</summary>
    private static void DrawArrow(DrawingContext context, IBrush brush, IBrush grip, Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1)
        {
            return;
        }

        var direction = new Vector(dx / length, dy / length);
        var side = new Vector(-direction.Y, direction.X) * 7;
        var back = to - (direction * 14);
        context.DrawLine(new Pen(brush, 3, lineCap: PenLineCap.Round), from, back);
        context.DrawGeometry(brush, null, ShapePainter.Outline([to, back + side, back - side], closed: true));
        context.DrawEllipse(grip, new Pen(brush, 2), to, 4, 4);
    }

    /// <summary>What a drag changes.</summary>
    public enum DragKind
    {
        None,
        Move,
        Vertex,
        Arrow,
    }

    private sealed class Drag(AreaShape area, DragKind kind, int vertex, Point start)
    {
        public AreaShape Area { get; } = area;

        public DragKind Kind { get; } = kind;

        public int Vertex { get; } = vertex;

        public Point Start { get; } = start;

        public IReadOnlyList<Point> Points { get; set; } = [];

        public double Angle { get; set; }
    }
}
