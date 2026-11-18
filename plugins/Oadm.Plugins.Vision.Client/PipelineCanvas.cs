using System.ComponentModel;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

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

        if (canvas.EmptyControlAt(world) is { } empty)
        {
            canvas.SelectedNode = empty.Node;
            canvas.OpenPickerInto(empty.Node, empty.Port);
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
            : canvas.EmptyControlAt(world) is not null ? VisionTexts.AddInto
            : canvas.CanEdit && canvas.OutputPortAt(world) is not null ? VisionTexts.DragToLink
            : null;
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
        }

        Cursor = canvas.CursorAt(world) switch
        {
            StandardCursorType.Cross => new Cursor(StandardCursorType.Cross),
            StandardCursorType.Hand => new Cursor(StandardCursorType.Hand),
            _ => null,
        };
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
                canvas.EndLinkDrag(canvas.NodeAt(world), world);
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

    // ---- Drawing (the shared look: NodeCanvasPainter; the eye, the control inputs and the counts are Vision Center's own) ----

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var theme = new OverlayTheme(this);
        var bounds = new Rect(Bounds.Size);
        if (Canvas is not { } canvas)
        {
            context.FillRectangle(theme.Brush("Oadm.CanvasBackgroundBrush"), bounds);
            return;
        }

        var view = new CanvasView(canvas.Zoom, canvas.PanX, canvas.PanY);
        NodeCanvasPainter.DrawBackground(context, theme, bounds, view);
        using (context.PushTransform(NodeCanvasPainter.Transform(view)))
        {
            foreach (var link in canvas.Links)
            {
                NodeCanvasPainter.DrawLink(context, theme, link.Start, link.End, link.IsSelected, link.IsControl);
            }

            if (canvas.LinkDragFrom is { } from)
            {
                var start = from.OutputPoint;
                context.DrawGeometry(null, new Pen(theme.Brush("Oadm.AccentBrush"), 1, new DashStyle([3, 2], 0)), NodeCanvasPainter.Curve(start, canvas.LinkDragTo));
            }

            var linkBrush = NodeCanvasPainter.LinkBrush(theme);
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

    private static void DrawCount(DrawingContext context, OverlayTheme theme, LinkViewModel link)
    {
        if (link.Count is not { } count)
        {
            return;
        }

        var middle = NodeCanvasGeometry.OnCurve(link.Start, link.End, 0.5, link.IsControl);
        var text = theme.Small(count, theme.Brush("Oadm.TextSecondaryBrush"));
        var box = new Rect(middle.X - (text.Width / 2) - 6, middle.Y - (text.Height / 2) - 1, text.Width + 12, text.Height + 2);
        context.DrawRectangle(theme.Brush("Oadm.CardBackgroundBrush"), new Pen(theme.Brush("Oadm.BorderBrush"), 1), box, box.Height / 2, box.Height / 2);
        context.DrawText(text, new Point(box.X + 6, box.Y + 1));
    }

    private void DrawNode(DrawingContext context, OverlayTheme theme, CanvasViewModel canvas, NodeViewModel node, IBrush linkBrush)
    {
        var rect = node.Rect;
        var colour = theme.Brush(node.BrushKey);
        var accent = theme.Brush("Oadm.AccentBrush");
        var fill = theme.Brush("Oadm.CanvasNodeBrush");
        using var dim = node.Fit == LinkFit.Dimmed ? context.PushOpacity(0.35) : default(DrawingContext.PushedState?);

        var border = node.HasError ? NodeCardBorder.Error
            : node.IsSelected || node.Fit == LinkFit.Fits ? NodeCardBorder.Accent
            : NodeCardBorder.Normal;
        var nameRight = node.CanShowOnVideo ? NodeCanvasGeometry.EyeRect(node.X, node.Y).X - 4 : rect.Right - 10;
        NodeCanvasPainter.DrawCard(context, this, theme, rect, colour, node.IconKey, node.Name, node.IsSelected, border, nameRight);

        if (node.CanShowOnVideo)
        {
            var eye = NodeCanvasGeometry.EyeRect(node.X, node.Y);
            NodeCanvasPainter.DrawIcon(context, this, node.ShowOnVideo ? "eye" : "eyeOff",
                theme.Brush(node.ShowOnVideo ? "Oadm.TextPrimaryBrush" : "Oadm.TextDisabledBrush"), eye.Deflate(4));
        }

        // While a link is dragged the inputs it fits light up: the data input or a control input.
        var dragged = canvas.LinkDragFrom;

        // The control inputs: a row each below the live line, the dot on the left edge, its label beside it. A pulse input is a
        // ring, a yes-or-no input a filled dot.
        var inputs = node.ControlInputs;
        for (var i = 0; i < inputs.Count; i++)
        {
            var at = NodeCanvasGeometry.ControlPort(node.X, node.Y, i);
            var lit = dragged is not null && node.Fit == LinkFit.Fits && canvas.FitsPort(dragged, node, inputs[i].Port);
            var condition = inputs[i].Accepts == Vision.Model.PortKind.Condition;
            var dot = lit ? accent : condition ? linkBrush : fill;
            context.DrawEllipse(dot, new Pen(lit ? accent : linkBrush, 1), at, NodeCanvasGeometry.ControlPortRadius, NodeCanvasGeometry.ControlPortRadius);
            var label = theme.Body(inputs[i].Label, theme.Brush("Oadm.TextSecondaryBrush"));
            context.DrawText(label, new Point(rect.X + 14, at.Y - (label.Height / 2)));

            // The selected node's empty inputs get a + on the left, like the + below an output: a click adds a Timer or a
            // yes-or-no node into the input.
            if (canvas.ShowsControlPlus(node) && canvas.ControlLinkOf(node, inputs[i].Port) is null)
            {
                var plus = NodeCanvasGeometry.ControlPlusRect(node.X, node.Y, i);
                context.DrawLine(new Pen(linkBrush, 1, new DashStyle([2, 2], 0)), new Point(plus.Right, at.Y), new Point(at.X - NodeCanvasGeometry.ControlPortRadius, at.Y));
                context.DrawRectangle(fill, new Pen(linkBrush, 1), plus, 3, 3);
                NodeCanvasPainter.DrawIcon(context, this, "add", theme.Brush("Oadm.TextSecondaryBrush"), plus.Deflate(3));
            }
        }

        NodeCanvasPainter.DrawLine(context, theme, rect, node.Live);

        if (node.HasInputPort)
        {
            var lit = dragged is not null && node.Fit == LinkFit.Fits && canvas.FitsPort(dragged, node, null);
            NodeCanvasPainter.DrawPort(context, theme, NodeCanvasGeometry.InputPort(node.X, node.Y), lit);
        }

        if (node.HasOutputPort)
        {
            var at = node.OutputPoint;
            NodeCanvasPainter.DrawPort(context, theme, at, ReferenceEquals(canvas.LinkDragFrom, node));
            if (canvas.ShowsPlus(node))
            {
                NodeCanvasPainter.DrawPlus(context, this, theme, at, canvas.PlusOf(node));
            }
        }

        if (node.Error is { } message)
        {
            NodeCanvasPainter.DrawBelow(context, theme, rect, message);
        }
    }

    /// <summary>The live line of a node as the canvas draws it (tests).</summary>
    internal static string LiveOf(NodeViewModel node) => node.Live.ToString(CultureInfo.InvariantCulture);
}
