using System.ComponentModel;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The node canvas (docs/spec/vision-center.md "Page"): our own control, no node editor exists for Avalonia. Draws the dot grid,
/// the links as vertical curves with their number of objects, the node cards (colour stripe, icon, name, eye, live line, input
/// dot at the top, output dot at the bottom, + below an output, the error directly below a red-bordered node: the graph runs
/// from top to bottom) and a link being dragged. Everything is
/// drawn from <see cref="CanvasViewModel"/>; a change of a live line only draws again, nothing is rebuilt. The input goes to
/// the view model: press, drag and release, the wheel, Delete, Ctrl+Z, Ctrl+Y, Ctrl+C, Ctrl+V, Esc. Colours from the theme.
/// </summary>
public sealed class PipelineCanvas : Control
{
    public static readonly StyledProperty<CanvasViewModel?> CanvasProperty =
        AvaloniaProperty.Register<PipelineCanvas, CanvasViewModel?>(nameof(Canvas));

    private const double DotStep = 22;
    private const double CornerRadius = 8;

    private Drag _drag;
    private CanvasViewModel? _fitted;
    private Point _pressAt;
    private Point _panStart;
    private Point _moveOffset;
    private NodeViewModel? _moving;
    private bool _dragMoved;

    static PipelineCanvas()
    {
        FocusableProperty.OverrideDefaultValue<PipelineCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PipelineCanvas>(true);
    }

    private enum Drag
    {
        None,
        Pan,
        Node,
        Link,
    }

    public CanvasViewModel? Canvas
    {
        get => GetValue(CanvasProperty);
        set => SetValue(CanvasProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == CanvasProperty)
        {
            if (change.OldValue is CanvasViewModel old)
            {
                old.PropertyChanged -= OnCanvasChanged;
            }

            if (change.NewValue is CanvasViewModel now)
            {
                now.PropertyChanged += OnCanvasChanged;
                now.ViewportSize = Bounds.Size;
                FitOnce(now);
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty && Canvas is { } canvas)
        {
            canvas.ViewportSize = Bounds.Size;
            FitOnce(canvas);
        }
    }

    /// <summary>A pipeline shown for the first time: every node in the view, once the canvas has its size.</summary>
    private void FitOnce(CanvasViewModel canvas)
    {
        if (!ReferenceEquals(_fitted, canvas) && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _fitted = canvas;
            canvas.FitView();
        }
    }

    private void OnCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasViewModel.Version) or nameof(CanvasViewModel.Zoom) or nameof(CanvasViewModel.PanX) or nameof(CanvasViewModel.PanY))
        {
            InvalidateVisual();
        }
    }

    // ---- Input routing ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (Canvas is not { } canvas || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var screen = e.GetPosition(this);
        var world = canvas.ToWorld(screen);
        _pressAt = screen;
        _dragMoved = false;
        e.Handled = true;
        if (canvas.PlusAt(world) is { } plusOf)
        {
            canvas.SelectedNode = plusOf;
            canvas.OpenPickerAfter(plusOf);
            return;
        }

        canvas.Picker.Close();
        if (canvas.EyeAt(world) is { } eyeOf)
        {
            canvas.ToggleEye(eyeOf);
            return;
        }

        if (canvas.CanEdit && canvas.OutputPortAt(world) is { } from)
        {
            canvas.BeginLinkDrag(from, world);
            _drag = Drag.Link;
            e.Pointer.Capture(this);
            return;
        }

        if (canvas.NodeAt(world) is { } node)
        {
            canvas.SelectedNode = node;
            if (canvas.CanEdit)
            {
                _moving = node;
                _moveOffset = new Point(world.X - node.X, world.Y - node.Y);
                canvas.BeginMove();
                _drag = Drag.Node;
                e.Pointer.Capture(this);
            }

            return;
        }

        if (canvas.LinkAt(world) is { } link)
        {
            canvas.SelectedLink = link;
            return;
        }

        _drag = Drag.Pan;
        _panStart = new Point(canvas.PanX, canvas.PanY);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (Canvas is not { } canvas)
        {
            return;
        }

        var screen = e.GetPosition(this);
        var world = canvas.ToWorld(screen);
        _dragMoved |= Math.Abs(screen.X - _pressAt.X) + Math.Abs(screen.Y - _pressAt.Y) > 3;
        switch (_drag)
        {
            case Drag.Pan:
                canvas.PanTo(_panStart.X + screen.X - _pressAt.X, _panStart.Y + screen.Y - _pressAt.Y);
                return;
            case Drag.Node when _moving is not null && _dragMoved:
                canvas.MoveNode(_moving, Math.Round(world.X - _moveOffset.X), Math.Round(world.Y - _moveOffset.Y));
                return;
            case Drag.Link:
                canvas.DragLinkTo(world);
                return;
        }

        var tip = canvas.EyeAt(world) is not null ? VisionTexts.ShowOnVideo
            : canvas.PlusAt(world) is not null ? VisionTexts.AddAfter
            : canvas.CanEdit && canvas.OutputPortAt(world) is not null ? VisionTexts.DragToLink
            : null;
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
        }

        Cursor = canvas.CanEdit && canvas.OutputPortAt(world) is not null ? new Cursor(StandardCursorType.Cross)
            : canvas.NodeAt(world) is not null || canvas.LinkAt(world) is not null ? new Cursor(StandardCursorType.Hand)
            : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (Canvas is not { } canvas)
        {
            return;
        }

        var world = canvas.ToWorld(e.GetPosition(this));
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Link:
                canvas.EndLinkDrag(canvas.NodeAt(world));
                break;
            case Drag.Node:
                canvas.EndMove();
                _moving = null;
                break;
            case Drag.Pan when !_dragMoved:
                canvas.ClearSelection();
                break;
        }

        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == Drag.Link)
        {
            Canvas?.EndLinkDrag(null);
        }
        else if (_drag == Drag.Node)
        {
            Canvas?.EndMove();
        }

        _drag = Drag.None;
        _moving = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (Canvas is { } canvas && e.Delta.Y != 0)
        {
            canvas.ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (Canvas is not { } canvas)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when canvas.DeleteSelectedCommand.CanExecute(null):
                canvas.DeleteSelectedCommand.Execute(null);
                break;
            case Key.Z when ctrl && canvas.UndoCommand.CanExecute(null):
                canvas.UndoCommand.Execute(null);
                break;
            case Key.Y when ctrl && canvas.RedoCommand.CanExecute(null):
                canvas.RedoCommand.Execute(null);
                break;
            case Key.C when ctrl && canvas.CopyCommand.CanExecute(null):
                canvas.CopyCommand.Execute(null);
                break;
            case Key.V when ctrl && canvas.PasteCommand.CanExecute(null):
                canvas.PasteCommand.Execute(null);
                break;
            case Key.Escape when canvas.Picker.IsOpen:
                canvas.Picker.Close();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var theme = new OverlayTheme(this);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(theme.Brush("Oadm.WindowBackgroundBrush"), bounds);
        if (Canvas is not { } canvas)
        {
            return;
        }

        DrawDots(context, theme, canvas, bounds);
        using (context.PushTransform(Matrix.CreateScale(canvas.Zoom, canvas.Zoom) * Matrix.CreateTranslation(canvas.PanX, canvas.PanY)))
        {
            var linkBrush = theme.Brush("Oadm.TextDisabledBrush");
            foreach (var link in canvas.Links)
            {
                DrawLink(context, theme, link, linkBrush);
            }

            if (canvas.LinkDragFrom is { } from)
            {
                var start = CanvasGeometry.OutputPort(from.X, from.Y);
                context.DrawGeometry(null, new Pen(theme.Brush("Oadm.AccentBrush"), 2, new DashStyle([3, 2], 0)), Curve(start, canvas.LinkDragTo));
            }

            foreach (var node in canvas.Nodes)
            {
                DrawNode(context, theme, canvas, node, linkBrush);
            }

            foreach (var link in canvas.Links)
            {
                DrawCount(context, theme, link);
            }
        }
    }

    private static void DrawDots(DrawingContext context, OverlayTheme theme, CanvasViewModel canvas, Rect bounds)
    {
        var step = DotStep * canvas.Zoom;
        if (step < 6)
        {
            return;
        }

        var dot = OverlayTheme.Tint(theme.Brush("Oadm.InputBorderBrush"), 0.9);
        var radius = Math.Max(0.8, 1.1 * canvas.Zoom);
        var startX = canvas.PanX % step;
        var startY = canvas.PanY % step;
        for (var x = startX; x < bounds.Width; x += step)
        {
            for (var y = startY; y < bounds.Height; y += step)
            {
                context.DrawEllipse(dot, null, new Point(x, y), radius, radius);
            }
        }
    }

    private static StreamGeometry Curve(Point from, Point to)
    {
        var (c1, c2) = CanvasGeometry.Curve(from, to);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false);
            ctx.CubicBezierTo(c1, c2, to);
            ctx.EndFigure(false);
        }

        return geometry;
    }

    private static void DrawLink(DrawingContext context, OverlayTheme theme, LinkViewModel link, IBrush brush)
    {
        var from = CanvasGeometry.OutputPort(link.From.X, link.From.Y);
        var to = CanvasGeometry.InputPort(link.To.X, link.To.Y);
        var pen = link.IsSelected ? new Pen(theme.Brush("Oadm.AccentBrush"), 3) : new Pen(brush, 2);
        context.DrawGeometry(null, pen, Curve(from, to));
    }

    private static void DrawCount(DrawingContext context, OverlayTheme theme, LinkViewModel link)
    {
        if (link.Count is not { } count)
        {
            return;
        }

        var middle = CanvasGeometry.OnCurve(CanvasGeometry.OutputPort(link.From.X, link.From.Y), CanvasGeometry.InputPort(link.To.X, link.To.Y), 0.5);
        var text = theme.Small(count, theme.Brush("Oadm.TextSecondaryBrush"));
        var box = new Rect(middle.X - (text.Width / 2) - 6, middle.Y - (text.Height / 2) - 1, text.Width + 12, text.Height + 2);
        context.DrawRectangle(theme.Brush("Oadm.CardBackgroundBrush"), new Pen(theme.Brush("Oadm.BorderBrush"), 1), box, box.Height / 2, box.Height / 2);
        context.DrawText(text, new Point(box.X + 6, box.Y + 1));
    }

    private void DrawNode(DrawingContext context, OverlayTheme theme, CanvasViewModel canvas, NodeViewModel node, IBrush linkBrush)
    {
        var rect = CanvasGeometry.NodeRect(node.X, node.Y);
        var colour = theme.Brush(node.BrushKey);
        var accent = theme.Brush("Oadm.AccentBrush");
        var error = theme.Brush("Oadm.StatusErrorBrush");
        var fill = theme.Brush("Oadm.InputBackgroundBrush");
        using var dim = node.Fit == LinkFit.Dimmed ? context.PushOpacity(0.35) : default(DrawingContext.PushedState?);

        if (node.IsSelected)
        {
            context.DrawRectangle(null, new Pen(OverlayTheme.Tint(accent, 0.35), 4), rect.Inflate(2), CornerRadius + 2, CornerRadius + 2);
        }

        var border = node.HasError ? new Pen(error, 1.5)
            : node.IsSelected || node.Fit == LinkFit.Fits ? new Pen(accent, 1.5)
            : new Pen(theme.Brush("Oadm.InputBorderBrush"), 1);
        context.DrawRectangle(fill, border, rect, CornerRadius, CornerRadius);

        // The kind's colour: a stripe along the left edge, inside the rounded corners.
        using (context.PushClip(new RoundedRect(rect, CornerRadius)))
        {
            context.FillRectangle(colour, new Rect(rect.X, rect.Y, 4, rect.Height));
        }

        DrawIcon(context, node.IconKey, colour, new Rect(rect.X + 14, rect.Y + 9, 16, 16));
        var nameRight = node.CanShowOnVideo ? CanvasGeometry.EyeRect(node.X, node.Y).X - 4 : rect.Right - 10;
        var name = theme.Body(node.Name, theme.Brush("Oadm.TextPrimaryBrush"), FontWeight.SemiBold);
        name.MaxTextWidth = Math.Max(10, nameRight - (rect.X + 38));
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(name, new Point(rect.X + 38, rect.Y + 9));

        if (node.CanShowOnVideo)
        {
            var eye = CanvasGeometry.EyeRect(node.X, node.Y);
            DrawIcon(context, node.ShowOnVideo ? "eye" : "eyeOff",
                theme.Brush(node.ShowOnVideo ? "Oadm.TextPrimaryBrush" : "Oadm.TextDisabledBrush"), eye.Deflate(4));
        }

        if (node.Live.Length > 0)
        {
            var live = theme.Body(node.Live, theme.Brush("Oadm.TextSecondaryBrush"));
            live.MaxTextWidth = rect.Width - 24;
            live.MaxLineCount = 1;
            live.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(live, new Point(rect.X + 14, rect.Y + 35));
        }

        if (node.HasInputPort)
        {
            var at = CanvasGeometry.InputPort(node.X, node.Y);
            var lit = node.Fit == LinkFit.Fits;
            context.DrawEllipse(lit ? accent : fill, new Pen(lit ? accent : linkBrush, 2), at, CanvasGeometry.PortRadius - 1, CanvasGeometry.PortRadius - 1);
        }

        if (node.HasOutputPort)
        {
            var at = CanvasGeometry.OutputPort(node.X, node.Y);
            var lit = ReferenceEquals(canvas.LinkDragFrom, node);
            context.DrawEllipse(lit ? accent : fill, new Pen(lit ? accent : linkBrush, 2), at, CanvasGeometry.PortRadius - 1, CanvasGeometry.PortRadius - 1);
            if (canvas.ShowsPlus(node))
            {
                var plus = canvas.PlusOf(node);
                context.DrawLine(new Pen(linkBrush, 1), new Point(at.X, at.Y + CanvasGeometry.PortRadius), new Point(plus.Center.X, plus.Y));
                context.DrawRectangle(fill, new Pen(linkBrush, 1), plus, 4, 4);
                DrawIcon(context, "add", theme.Brush("Oadm.TextSecondaryBrush"), plus.Deflate(5));
            }
        }

        if (node.Error is { } message)
        {
            var text = theme.Small(message, error);
            text.MaxTextWidth = CanvasGeometry.NodeWidth + 30;
            // On the canvas's background: the link down from the output passes behind the error, never through it.
            context.FillRectangle(theme.Brush("Oadm.WindowBackgroundBrush"), new Rect(rect.X - 2, rect.Bottom + 4, text.Width + 4, text.Height + 2), 3);
            context.DrawText(text, new Point(rect.X, rect.Bottom + 5));
        }
    }

    private void DrawIcon(DrawingContext context, string key, IBrush brush, Rect at)
    {
        if (!this.TryFindResource("Icon." + key, ActualThemeVariant, out var value) || value is not Avalonia.Media.Geometry geometry)
        {
            return;
        }

        var scale = Math.Min(at.Width, at.Height) / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }

    /// <summary>The live line of a node as the canvas draws it (tests).</summary>
    internal static string LiveOf(NodeViewModel node) => node.Live.ToString(CultureInfo.InvariantCulture);
}
