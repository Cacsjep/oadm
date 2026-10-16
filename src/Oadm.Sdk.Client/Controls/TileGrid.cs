using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>One tile of a <see cref="TileGrid"/>: its place, what it shows and how it is removed while editing.</summary>
public interface ITileGridItem : INotifyPropertyChanged
{
    /// <summary>The tile's cells; raise PropertyChanged for <c>Rect</c> when it changes.</summary>
    TileRect Rect { get; }

    /// <summary>The name in the edit frame's header ("10.0.0.48").</summary>
    string? Title { get; }

    /// <summary>What the tile shows (a live video, a widget); null shows <see cref="Message"/>.</summary>
    Control? Content { get; }

    /// <summary>A text in the middle of the tile while there is no <see cref="Content"/> ("Removed device 1a2b3c4d").</summary>
    string? Message { get; }

    /// <summary>The X of the edit frame.</summary>
    ICommand? RemoveCommand { get; }
}

/// <summary>What a <see cref="TileGrid"/> shows: the tiles, the edit mode and the rules; it moves and resizes tiles.</summary>
public interface ITileGridSource : INotifyPropertyChanged
{
    /// <summary>The tiles; when the list also raises collection changes, the grid follows them.</summary>
    IReadOnlyList<ITileGridItem> Items { get; }

    /// <summary>While true the grid draws the cell lines and every tile its edit frame (PropertyChanged <c>IsEditing</c>).</summary>
    bool IsEditing { get; }

    /// <summary>The grid's rules (size, smallest tile).</summary>
    TileLayout Layout { get; }

    /// <summary>The tooltip of the edit frame's X ("Remove camera").</summary>
    string RemoveText { get; }

    /// <summary>Tiles moved or resized: the grid arranges again.</summary>
    event EventHandler? LayoutChanged;

    /// <summary>Moves or resizes a tile; false (nothing changes) when it would break the rules.</summary>
    bool TryMove(ITileGridItem item, TileRect rect);
}

/// <summary>
/// A grid of tiles that always fills its space (Live View's views, dashboards): <see cref="TileLayout.Size"/> x
/// <see cref="TileLayout.Size"/> cells, a <see cref="TileFrame"/> per tile on its cells. While editing it draws the cell
/// lines, each frame moves (drag inside), resizes (drag an edge or a corner) and removes its tile, and
/// <see cref="ShowPreview"/> shows where a dragged item would land. Theme classes: <c>Panel.tile</c>, <c>Border.tileEdit</c>,
/// <c>Border.tileHover</c>, <c>TextBlock.tileTitle</c>, <c>Border.dropPreview</c>.
/// </summary>
public sealed class TileGrid : Panel
{
    private readonly GridLines _lines;
    private readonly Border _preview = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly TextBlock _previewTitle = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private TileRect? _previewRect;
    private ITileGridSource? _source;

    /// <summary>An empty grid; set <see cref="Source"/>.</summary>
    public TileGrid()
    {
        _lines = new GridLines(this) { IsHitTestVisible = false, IsVisible = false };
        _preview.Classes.Add("dropPreview");
        _preview.Child = _previewTitle;
        Children.Add(_lines);
        Children.Add(_preview);
    }

    /// <summary>The tiles and rules shown.</summary>
    public ITileGridSource? Source
    {
        get => _source;
        set
        {
            if (_source is not null)
            {
                if (_source.Items is INotifyCollectionChanged old)
                {
                    old.CollectionChanged -= OnItemsChanged;
                }

                _source.LayoutChanged -= OnLayoutChanged;
                _source.PropertyChanged -= OnSourceChanged;
            }

            _source = value;
            foreach (var frame in Children.OfType<TileFrame>().ToList())
            {
                Children.Remove(frame);
                frame.Item = null;
            }

            _lines.IsVisible = _source?.IsEditing == true;
            if (_source is not null)
            {
                if (_source.Items is INotifyCollectionChanged items)
                {
                    items.CollectionChanged += OnItemsChanged;
                }

                _source.LayoutChanged += OnLayoutChanged;
                _source.PropertyChanged += OnSourceChanged;
                Sync();
            }
        }
    }

    private int N => _source?.Layout.Size ?? 1;

    /// <summary>Where a dragged item would land, with its name (null hides it).</summary>
    public void ShowPreview(TileRect? rect, string? title = null)
    {
        _previewRect = rect;
        _previewTitle.Text = title;
        _preview.IsVisible = rect is not null;
        _preview.ZIndex = 1;
        InvalidateArrange();
    }

    /// <summary>The cell under <paramref name="point"/> (relative to the grid).</summary>
    public (int X, int Y) CellAt(Point point) =>
        (Math.Clamp((int)(point.X / CellWidth), 0, N - 1), Math.Clamp((int)(point.Y / CellHeight), 0, N - 1));

    /// <summary>Whether <paramref name="point"/> (relative to the grid) lies on the grid.</summary>
    public bool Contains(Point point) => point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height;

    /// <summary>The width of one cell.</summary>
    public double CellWidth => Bounds.Width / N;

    /// <summary>The height of one cell.</summary>
    public double CellHeight => Bounds.Height / N;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _lines.Measure(availableSize);
        _preview.Measure(availableSize);
        foreach (var child in Children)
        {
            if (child is TileFrame { Item: { } item })
            {
                child.Measure(new Size(availableSize.Width / N * item.Rect.Width, availableSize.Height / N * item.Rect.Height));
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = finalSize.Width / N;
        var h = finalSize.Height / N;
        _lines.Arrange(new Rect(finalSize));
        if (_previewRect is { } p)
        {
            _preview.Arrange(Cells(p, w, h));
        }

        foreach (var child in Children)
        {
            if (child is TileFrame { Item: { } item })
            {
                child.Arrange(Cells(item.Rect, w, h));
            }
        }

        return finalSize;
    }

    private static Rect Cells(TileRect r, double w, double h) =>
        new(Math.Round(r.X * w), Math.Round(r.Y * h), Math.Round(r.Width * w), Math.Round(r.Height * h));

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnLayoutChanged(object? sender, EventArgs e) => InvalidateArrange();

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITileGridSource.IsEditing))
        {
            _lines.IsVisible = _source?.IsEditing == true;
        }
    }

    /// <summary>One frame per tile; frames of tiles that stay keep their content.</summary>
    private void Sync()
    {
        var items = _source?.Items.ToList() ?? [];
        var keep = items.ToHashSet();
        var frames = new Dictionary<ITileGridItem, TileFrame>();
        foreach (var frame in Children.OfType<TileFrame>().ToList())
        {
            if (frame.Item is { } item && keep.Contains(item))
            {
                frames[item] = frame;
            }
            else
            {
                Children.Remove(frame);
                frame.Item = null;
            }
        }

        foreach (var item in items.Where(i => !frames.ContainsKey(i)))
        {
            Children.Add(new TileFrame(this) { Item = item });
        }

        InvalidateMeasure();
    }

    /// <summary>The cell lines while the grid is edited.</summary>
    private sealed class GridLines(TileGrid grid) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (!this.TryFindResource("Oadm.BorderBrush", ActualThemeVariant, out var brush) || brush is not IBrush b)
            {
                return;
            }

            var n = grid.N;
            var pen = new Pen(b, 1);
            for (var i = 1; i < n; i++)
            {
                var x = Math.Round(i * Bounds.Width / n) + 0.5;
                var y = Math.Round(i * Bounds.Height / n) + 0.5;
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }
    }
}

/// <summary>
/// One tile of a <see cref="TileGrid"/>: its content, or its message when there is none. While editing, a frame over it
/// moves the tile (drag inside), resizes it (drag an edge or a corner, the cursor shows it) and removes it (the X top
/// right); everything snaps to the cells. Outside editing the pointer over it shows a thin accent frame.
/// </summary>
public sealed class TileFrame : Panel
{
    /// <summary>How close to the frame's border a press resizes instead of moving.</summary>
    private const double EdgeBand = 8;

    private static readonly Dictionary<TileEdges, Cursor> Cursors = new()
    {
        [TileEdges.None] = new Cursor(StandardCursorType.SizeAll),
        [TileEdges.Left] = new Cursor(StandardCursorType.SizeWestEast),
        [TileEdges.Right] = new Cursor(StandardCursorType.SizeWestEast),
        [TileEdges.Top] = new Cursor(StandardCursorType.SizeNorthSouth),
        [TileEdges.Bottom] = new Cursor(StandardCursorType.SizeNorthSouth),
        [TileEdges.Left | TileEdges.Top] = new Cursor(StandardCursorType.TopLeftCorner),
        [TileEdges.Right | TileEdges.Top] = new Cursor(StandardCursorType.TopRightCorner),
        [TileEdges.Left | TileEdges.Bottom] = new Cursor(StandardCursorType.BottomLeftCorner),
        [TileEdges.Right | TileEdges.Bottom] = new Cursor(StandardCursorType.BottomRightCorner),
    };

    private readonly TileGrid _grid;
    private readonly ContentControl _content = new();
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Border _edit = new();
    private readonly Border _hover = new() { IsHitTestVisible = false };
    private readonly TextBlock _title = new();
    private readonly Button _remove = new();
    private ITileGridItem? _item;
    private ITileGridSource? _source;
    private Point _start;
    private TileRect? _startRect;
    private TileEdges _edges;

    /// <summary>A frame in <paramref name="grid"/> (the grid creates them).</summary>
    public TileFrame(TileGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
        Classes.Add("tile");
        _message.Classes.Add("secondary");

        _title.Classes.Add("tileTitle");
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _remove.Classes.Add("toolbar");
        _remove.Classes.Add("iconOnly");
        _remove.HorizontalAlignment = HorizontalAlignment.Right;
        _remove.VerticalAlignment = VerticalAlignment.Top;
        _remove.Content = new OadmIcon { Width = 12, Height = 12 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(_remove, 1);
        header.Children.AddRange([_title, _remove]);
        _edit.Classes.Add("tileEdit");
        _edit.Cursor = Cursors[TileEdges.None];
        _edit.Child = header;
        _edit.IsVisible = false;

        _hover.Classes.Add("tileHover"); // the accent frame while the pointer is over the tile (theme)
        Children.AddRange([_content, _message, _hover, _edit]);
        _edit.PointerPressed += OnEditPressed;
        _edit.PointerMoved += OnEditMoved;
        _edit.PointerReleased += OnEditReleased;
    }

    /// <summary>The tile shown.</summary>
    public ITileGridItem? Item
    {
        get => _item;
        set
        {
            if (_item is not null)
            {
                _item.PropertyChanged -= OnItemChanged;
            }

            if (_source is not null)
            {
                _source.PropertyChanged -= OnSourceChanged;
                _source = null;
            }

            _item = value;
            _content.Content = null;
            if (_item is not null)
            {
                _item.PropertyChanged += OnItemChanged;
                _source = _grid.Source;
                if (_source is not null)
                {
                    _source.PropertyChanged += OnSourceChanged;
                    ToolTip.SetTip(_remove, _source.RemoveText);
                }

                _remove.Command = _item.RemoveCommand;
            }

            Update();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remove.Content is OadmIcon icon && this.TryFindResource("Icon.close", ActualThemeVariant, out var x) && x is Geometry g)
        {
            icon.Data = g;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITileGridItem.Content) or nameof(ITileGridItem.Message) or nameof(ITileGridItem.Title) or null or "")
        {
            Update();
        }
        else if (e.PropertyName == nameof(ITileGridItem.Rect))
        {
            _grid.InvalidateMeasure();
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITileGridSource.IsEditing))
        {
            Update();
        }
    }

    private void Update()
    {
        var item = _item;
        var content = item?.Content;
        if (!ReferenceEquals(_content.Content, content))
        {
            _content.Content = content;
        }

        _message.Text = content is null ? item?.Message : null;
        _message.IsVisible = !string.IsNullOrEmpty(_message.Text);
        _title.Text = item?.Title;
        _edit.IsVisible = item is not null && _source?.IsEditing == true;
        Classes.Set("editing", _edit.IsVisible); // the edit frame has its own border: no hover frame
    }

    private void OnEditPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_item is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button or OadmIcon)
        {
            return;
        }

        _start = e.GetPosition(_grid);
        _startRect = _item.Rect;
        _edges = EdgesAt(e.GetPosition(_edit));
        e.Pointer.Capture(_edit);
        e.Handled = true;
    }

    private void OnEditMoved(object? sender, PointerEventArgs e)
    {
        if (_item is null || _startRect is not { } start || _source is not { } source || _grid.CellWidth <= 0 || _grid.CellHeight <= 0)
        {
            _edit.Cursor = Cursors[EdgesAt(e.GetPosition(_edit))]; // the cursor shows what a press would do
            return;
        }

        var delta = e.GetPosition(_grid) - _start;
        var dx = (int)Math.Round(delta.X / _grid.CellWidth);
        var dy = (int)Math.Round(delta.Y / _grid.CellHeight);
        source.TryMove(_item, source.Layout.Drag(start, _edges, dx, dy));
    }

    private void OnEditReleased(object? sender, PointerReleasedEventArgs e)
    {
        _startRect = null;
        _edges = TileEdges.None;
        e.Pointer.Capture(null);
    }

    /// <summary>The edges within <see cref="EdgeBand"/> of <paramref name="point"/> (a corner is two).</summary>
    private TileEdges EdgesAt(Point point)
    {
        var size = _edit.Bounds.Size;
        var edges = TileEdges.None;
        if (point.X <= EdgeBand)
        {
            edges |= TileEdges.Left;
        }
        else if (point.X >= size.Width - EdgeBand)
        {
            edges |= TileEdges.Right;
        }

        if (point.Y <= EdgeBand)
        {
            edges |= TileEdges.Top;
        }
        else if (point.Y >= size.Height - EdgeBand)
        {
            edges |= TileEdges.Bottom;
        }

        return edges;
    }
}
