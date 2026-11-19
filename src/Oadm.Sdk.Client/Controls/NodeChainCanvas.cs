using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One node of a <see cref="NodeChain"/>: its name, icon and colour, the line below the name, a status icon at the top right,
/// a message directly below the card (an error in red, a warning in amber) and its tooltip.
/// </summary>
/// <param name="Name">The name on the card ("Set IP").</param>
/// <param name="IconKey">The theme icon without <c>Icon.</c> ("network").</param>
/// <param name="BrushKey">The theme brush of the stripe and the icon ("Oadm.StatusOkBrush", "Oadm.Tag.BlueBrush").</param>
public sealed record NodeChainNode(string Name, string IconKey, string BrushKey)
{
    /// <summary>The line below the name (a summary, a result), trimmed to the card; the whole text in <see cref="Tip"/>.</summary>
    public string? Line { get; init; }

    /// <summary>A status icon at the top right ("status.ok"), or null.</summary>
    public string? StatusIconKey { get; init; }

    /// <summary>The brush of the status icon.</summary>
    public string? StatusBrushKey { get; init; }

    /// <summary>A message directly below the card, wrapped (the profile check's error, a failed result), or null.</summary>
    public string? Below { get; init; }

    /// <summary>The brush of <see cref="Below"/>; null: the error colour, and the card gets the red border.</summary>
    public string? BelowBrushKey { get; init; }

    /// <summary>The card is drawn dimmed (a node that did not run).</summary>
    public bool IsDimmed { get; init; }

    /// <summary>The tooltip of the card (the whole line), or null.</summary>
    public string? Tip { get; init; }

    /// <summary>
    /// A node may follow this one: while the chain <see cref="NodeChain.CanAdd"/>, the + below the card shows on the last card
    /// and on the selected one (beside the link), and a click on it raises <see cref="NodeChain.AddRequested"/>.
    /// </summary>
    public bool CanAddAfter { get; init; }

    /// <summary>The card has a red border (its message below is an error).</summary>
    public bool HasError => Below is not null && BelowBrushKey is null;
}

/// <summary>
/// A straight chain of nodes on a node canvas, top to bottom (a Provisioning profile, one camera's flow): the nodes, the
/// selected one and the view. Set <see cref="Nodes"/> to change what is drawn; <see cref="NodeChainCanvas"/> draws it with
/// the look of every node canvas (<see cref="NodeCanvasPainter"/>) and selects on a click.
/// </summary>
public sealed partial class NodeChain : ObservableObject
{
    /// <summary>The nodes from top to bottom.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<NodeChainNode> Nodes { get; set; } = [];

    /// <summary>The selected node; -1: none. A click on a card sets it, a click on the background clears it (when <see cref="CanSelect"/>).</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>Clicks select nodes (the editor); false: read only, only pan and zoom.</summary>
    [ObservableProperty]
    public partial bool CanSelect { get; set; } = true;

    /// <summary>The view (zoom and pan); the canvas fits every node in the view when the number of nodes changes.</summary>
    [ObservableProperty]
    public partial CanvasView View { get; set; } = new(1, 0, 0);

    /// <summary>Rises to ask the canvas to fit every node in the view again (Fit view).</summary>
    [ObservableProperty]
    public partial int FitRequest { get; set; }

    /// <summary>The + below the cards that may be followed (<see cref="NodeChainNode.CanAddAfter"/>) shows (the editor).</summary>
    [ObservableProperty]
    public partial bool CanAdd { get; set; }

    /// <summary>The tooltip of the + below a card.</summary>
    [ObservableProperty]
    public partial string AddTip { get; set; } = "Add a node after this one";

    /// <summary>The + below a card was clicked: open the node list there (<see cref="NodePicker"/>).</summary>
    public event EventHandler<NodeChainAddEventArgs>? AddRequested;

    /// <summary>Every node in the view again.</summary>
    public void FitView() => FitRequest++;

    /// <summary>The + below the card at <paramref name="index"/>: raises <see cref="AddRequested"/> (the canvas on a click, tests).</summary>
    /// <param name="index">The card whose + was clicked; the new node goes right after it.</param>
    /// <param name="at">Where the node list opens: right of the + (pixels of the canvas control).</param>
    public void RequestAdd(int index, Point at) => AddRequested?.Invoke(this, new NodeChainAddEventArgs(index, at));

    /// <summary>The + shows below the card at <paramref name="index"/>: it may be followed and it is the last or the selected one.</summary>
    public bool ShowsPlus(int index) =>
        CanAdd && index >= 0 && index < Nodes.Count && Nodes[index].CanAddAfter && (index == Nodes.Count - 1 || index == SelectedIndex);
}

/// <summary>The + below a card of a <see cref="NodeChain"/> was clicked.</summary>
/// <param name="index">The card whose + was clicked; the new node goes right after it.</param>
/// <param name="at">Where the node list opens: right of the + (pixels of the canvas control).</param>
public sealed class NodeChainAddEventArgs(int index, Point at) : EventArgs
{
    /// <summary>The card whose + was clicked; the new node goes right after it.</summary>
    public int Index { get; } = index;

    /// <summary>Where the node list opens: right of the + (pixels of the canvas control).</summary>
    public Point At { get; } = at;
}

/// <summary>
/// The canvas of a <see cref="NodeChain"/>: the cards top to bottom in the middle, linked from the output dot of one to the
/// input dot of the next, the message of a card directly below it, a status icon at a card's top right. Drag the background
/// to pan, the wheel zooms, a click selects a card, a click on the + below a card (while <see cref="NodeChain.CanAdd"/>) raises
/// <see cref="NodeChain.AddRequested"/>. Shared look with Vision Center's canvas (<see cref="NodeCanvasPainter"/>).
/// </summary>
public sealed class NodeChainCanvas : Control
{
    public static readonly StyledProperty<NodeChain?> ChainProperty =
        AvaloniaProperty.Register<NodeChainCanvas, NodeChain?>(nameof(Chain));

    /// <summary>The width of a card: wider than a pipeline's, a result message needs the room.</summary>
    public const double CardWidth = 300;

    /// <summary>The gap between a card (or its message) and the next card.</summary>
    public const double Gap = 40;

    /// <summary>Fit view leaves at most this much above the first card (screen pixels).</summary>
    public const double TopRoom = 24;

    /// <summary>Fit view never makes the cards smaller than this: a long chain is panned instead.</summary>
    public const double MinFitZoom = 0.8;

    private readonly List<Rect> _cards = [];
    private readonly List<double> _belows = [];
    private Rect _content;
    private int _fittedCount = -1;
    private IReadOnlyList<NodeChainNode>? _laidOut;
    private int _fittedRequest = -1;
    private bool _panning;
    private bool _moved;
    private Point _pressAt;
    private CanvasView _panStart;

    static NodeChainCanvas()
    {
        FocusableProperty.OverrideDefaultValue<NodeChainCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<NodeChainCanvas>(true);
    }

    /// <summary>The chain it draws: its nodes, the selected one and the view.</summary>
    public NodeChain? Chain
    {
        get => GetValue(ChainProperty);
        set => SetValue(ChainProperty, value);
    }

    /// <summary>Where the cards are (canvas units), after the last layout (tests).</summary>
    public IReadOnlyList<Rect> Cards => _cards;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ChainProperty)
        {
            if (change.OldValue is NodeChain old)
            {
                old.PropertyChanged -= OnChainChanged;
            }

            if (change.NewValue is NodeChain now)
            {
                now.PropertyChanged += OnChainChanged;
            }

            _fittedCount = -1;
            Relayout();
        }
        else if (change.Property == BoundsProperty)
        {
            _fittedCount = -1;
            Relayout();
        }
    }

    private void OnChainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeChain.Nodes) or nameof(NodeChain.FitRequest) or nameof(NodeChain.CanAdd))
        {
            Relayout();
        }
        else
        {
            if (e.PropertyName == nameof(NodeChain.SelectedIndex) && Chain is { } selected)
            {
                BringIntoView(selected);
            }

            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Relayout();
    }

    /// <summary>Places the cards (outside the render pass: a fit changes the view) and draws again.</summary>
    private void Relayout()
    {
        if (Chain is { } chain)
        {
            Layout(new CanvasTheme(this), chain.Nodes);
            FitIfNeeded(chain);
        }

        InvalidateVisual();
    }

    /// <summary>The cards from top to bottom, centred on x = 0; each card's message below it moves the next one down.</summary>
    private void Layout(CanvasTheme theme, IReadOnlyList<NodeChainNode> nodes)
    {
        _laidOut = nodes;
        _cards.Clear();
        _belows.Clear();
        var y = 0.0;
        foreach (var node in nodes)
        {
            var rect = new Rect(-CardWidth / 2, y, CardWidth, NodeCanvasGeometry.NodeHeight);
            _cards.Add(rect);
            var below = node.Below is { } text ? NodeCanvasPainter.BelowHeight(theme, text, CardWidth) : 0;
            _belows.Add(below);
            y = rect.Bottom + Gap + below;
        }

        // The + below the last card is part of the content (Fit view keeps it in the view).
        var plus = nodes.Count > 0 && nodes[^1].CanAddAfter && Chain is { CanAdd: true } ? NodeCanvasGeometry.PlusGap + NodeCanvasGeometry.PlusSize : 0;
        _content = _cards.Count == 0 ? default : new Rect(-CardWidth / 2 - 30, -30, CardWidth + 60, y - Gap + 60 + plus);
    }

    /// <summary>
    /// Where the + below the card at <paramref name="index"/> is (canvas units): below the last card and its message
    /// (<see cref="NodeCanvasGeometry.PlusRect"/>), on the straight link in the gap below any other card.
    /// </summary>
    public Rect PlusRect(int index)
    {
        if (index < 0 || index >= _cards.Count)
        {
            return default;
        }

        var card = _cards[index];
        var last = index == _cards.Count - 1;
        var plus = NodeCanvasGeometry.PlusRect(card.Center.X - (NodeCanvasGeometry.NodeWidth / 2), card.Y, height: card.Height);
        return last
            ? plus.Translate(new Vector(0, _belows[index]))
            : plus.WithY(card.Bottom + _belows[index] + ((Gap - NodeCanvasGeometry.PlusSize) / 2));
    }

    /// <summary>The card whose + is under a point of the control; -1: none.</summary>
    public int PlusIndexAt(Point screen)
    {
        if (Chain is not { } chain)
        {
            return -1;
        }

        var world = chain.View.ToWorld(screen);
        for (var i = 0; i < _cards.Count; i++)
        {
            if (chain.ShowsPlus(i) && PlusRect(i).Inflate(2).Contains(world))
            {
                return i;
            }
        }

        return -1;
    }

    private void FitIfNeeded(NodeChain chain)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _cards.Count == 0)
        {
            return;
        }

        if (_fittedCount != _cards.Count || _fittedRequest != chain.FitRequest)
        {
            _fittedCount = _cards.Count;
            _fittedRequest = chain.FitRequest;
            var view = NodeCanvasGeometry.Fit(_content, Bounds.Size, TopRoom);
            if (view.Zoom < MinFitZoom)
            {
                // A long chain stays readable: its top in the view, the rest a drag or the wheel away.
                view = new CanvasView(MinFitZoom, (Bounds.Width / 2) - (_content.Center.X * MinFitZoom), TopRoom - (_content.Y * MinFitZoom));
            }

            chain.View = view;
            BringIntoView(chain);
        }
    }

    /// <summary>Pans the selected card into the view when it is outside (a long chain).</summary>
    private void BringIntoView(NodeChain chain)
    {
        if (chain.SelectedIndex < 0 || chain.SelectedIndex >= _cards.Count || Bounds.Height <= 0)
        {
            return;
        }

        var view = chain.View;
        var card = _cards[chain.SelectedIndex];
        var top = (card.Top * view.Zoom) + view.PanY;
        var bottom = (card.Bottom * view.Zoom) + view.PanY;
        if (top < 0 || bottom > Bounds.Height)
        {
            chain.View = view with { PanY = (Bounds.Height / 2) - (card.Center.Y * view.Zoom) };
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var theme = new CanvasTheme(this);
        var bounds = new Rect(Bounds.Size);
        if (Chain is not { } chain)
        {
            context.FillRectangle(theme.Brush("Oadm.CanvasBackgroundBrush"), bounds);
            return;
        }

        var nodes = chain.Nodes;
        if (!ReferenceEquals(_laidOut, nodes))
        {
            Layout(theme, nodes);
        }

        var view = chain.View;
        NodeCanvasPainter.DrawBackground(context, theme, bounds, view);
        using (context.PushTransform(NodeCanvasPainter.Transform(view)))
        {
            for (var i = 1; i < _cards.Count; i++)
            {
                var from = new Point(_cards[i - 1].Center.X, _cards[i - 1].Bottom);
                var to = new Point(_cards[i].Center.X, _cards[i].Top);
                NodeCanvasPainter.DrawLink(context, theme, from, to, isSelected: false);
            }

            // The + below the cards before the cards: a card's message covers the line down to it like a link.
            for (var i = 0; i < _cards.Count; i++)
            {
                if (chain.ShowsPlus(i))
                {
                    NodeCanvasPainter.DrawPlus(context, this, theme, new Point(_cards[i].Center.X, _cards[i].Bottom), PlusRect(i));
                }
            }

            for (var i = 0; i < _cards.Count; i++)
            {
                DrawNode(context, theme, nodes[i], _cards[i], i == chain.SelectedIndex, i > 0, i < _cards.Count - 1 || chain.ShowsPlus(i));
            }
        }
    }

    private void DrawNode(DrawingContext context, CanvasTheme theme, NodeChainNode node, Rect rect, bool selected, bool hasInput, bool hasOutput)
    {
        using var dim = node.IsDimmed ? context.PushOpacity(0.55) : default(DrawingContext.PushedState?);
        var border = node.HasError ? NodeCardBorder.Error : selected ? NodeCardBorder.Accent : NodeCardBorder.Normal;
        var status = node.StatusIconKey is not null ? new Rect(rect.Right - 26, rect.Y + 9, 16, 16) : (Rect?)null;
        NodeCanvasPainter.DrawCard(context, this, theme, rect, theme.Brush(node.BrushKey), node.IconKey, node.Name, selected, border,
            status is { } s ? s.X - 6 : null);
        if (status is { } at && node.StatusIconKey is { } key)
        {
            NodeCanvasPainter.DrawIcon(context, this, key, theme.Brush(node.StatusBrushKey ?? node.BrushKey), at);
        }

        NodeCanvasPainter.DrawLine(context, theme, rect, node.Line ?? string.Empty);
        if (hasInput)
        {
            NodeCanvasPainter.DrawPort(context, theme, new Point(rect.Center.X, rect.Top));
        }

        if (hasOutput)
        {
            NodeCanvasPainter.DrawPort(context, theme, new Point(rect.Center.X, rect.Bottom));
        }

        if (node.Below is { } below)
        {
            NodeCanvasPainter.DrawBelow(context, theme, rect, below, node.BelowBrushKey is { } brush ? theme.Brush(brush) : null, CardWidth);
        }
    }

    /// <summary>The card under a point of the control; -1: none.</summary>
    public int IndexAt(Point screen)
    {
        if (Chain is not { } chain)
        {
            return -1;
        }

        var world = chain.View.ToWorld(screen);
        for (var i = 0; i < _cards.Count; i++)
        {
            if (_cards[i].Contains(world))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (Chain is not { } chain || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _pressAt = e.GetPosition(this);
        _moved = false;
        _panning = true;
        _panStart = chain.View;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (Chain is not { } chain)
        {
            return;
        }

        var screen = e.GetPosition(this);
        if (_panning)
        {
            _moved |= Math.Abs(screen.X - _pressAt.X) + Math.Abs(screen.Y - _pressAt.Y) > 3;
            if (_moved)
            {
                chain.View = _panStart with { PanX = _panStart.PanX + screen.X - _pressAt.X, PanY = _panStart.PanY + screen.Y - _pressAt.Y };
            }

            return;
        }

        var plus = PlusIndexAt(screen);
        var index = plus >= 0 ? -1 : IndexAt(screen);
        var tip = plus >= 0 ? chain.AddTip : index >= 0 ? chain.Nodes[index].Tip : null;
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
        }

        Cursor = plus >= 0 || (index >= 0 && chain.CanSelect) ? new Cursor(StandardCursorType.Hand) : null;
    }

    /// <summary>
    /// Where the node list of the + below the card at <paramref name="index"/> opens (pixels of the control): right of the +,
    /// its top level with the +, higher when the list would not fit above the bottom.
    /// </summary>
    public Point PickerPlace(int index)
    {
        var rect = PlusRect(index);
        var view = Chain?.View ?? new CanvasView(1, 0, 0);
        var top = (rect.Top * view.Zoom) + view.PanY - 10;
        return new Point((rect.Right * view.Zoom) + view.PanX + 8, Math.Max(8, Math.Min(top, Bounds.Height - NodePicker.TallestHeight - 8)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (Chain is { } chain && _panning && !_moved)
        {
            var screen = e.GetPosition(this);
            if (PlusIndexAt(screen) is var plus and >= 0)
            {
                chain.RequestAdd(plus, PickerPlace(plus));
            }
            else if (chain.CanSelect)
            {
                chain.SelectedIndex = IndexAt(screen);
            }
        }

        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (Chain is { } chain && e.Delta.Y != 0 && NodeCanvasGeometry.ZoomAround(chain.View, e.GetPosition(this), e.Delta.Y > 0 ? 1.1 : 1 / 1.1) is { } view)
        {
            chain.View = view;
            e.Handled = true;
        }
    }
}
