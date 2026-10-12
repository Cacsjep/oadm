using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>
/// The grid of one view: 20 x 20 cells that always fill the tab, a <see cref="TileFrame"/> per camera on its cells. While
/// editing it draws the cell lines and tells where a pointer is in cells.
/// </summary>
public sealed class ViewGrid : Panel
{
    private const int N = LiveViewPluginInfo.GridSize;
    private readonly GridLines _lines = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly Border _preview = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly TextBlock _previewTitle = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ViewTile? _previewTile;
    private ViewTab? _tab;

    public ViewGrid()
    {
        _preview.Classes.Add("dropPreview");
        _preview.Child = _previewTitle;
        Children.Add(_lines);
        Children.Add(_preview);
    }

    /// <summary>Where a dragged camera would land, with its address (null hides it).</summary>
    public void ShowPreview(ViewTile? tile, string? title = null)
    {
        _previewTile = tile;
        _previewTitle.Text = title;
        _preview.IsVisible = tile is not null;
        _preview.ZIndex = 1;
        InvalidateArrange();
    }

    public ViewTab? Tab
    {
        get => _tab;
        set
        {
            if (_tab is not null)
            {
                _tab.Tiles.CollectionChanged -= OnTilesChanged;
                _tab.LayoutChanged -= OnLayoutChanged;
                _tab.PropertyChanged -= OnTabChanged;
            }

            _tab = value;
            Children.RemoveAll(Children.OfType<TileFrame>().ToList());
            _lines.IsVisible = _tab?.IsEditing == true;
            if (_tab is not null)
            {
                _tab.Tiles.CollectionChanged += OnTilesChanged;
                _tab.LayoutChanged += OnLayoutChanged;
                _tab.PropertyChanged += OnTabChanged;
                Sync();
            }

        }
    }

    /// <summary>The cell under <paramref name="point"/> (relative to the grid).</summary>
    public (int X, int Y) CellAt(Point point) =>
        (Math.Clamp((int)(point.X / CellWidth), 0, N - 1), Math.Clamp((int)(point.Y / CellHeight), 0, N - 1));

    public double CellWidth => Bounds.Width / N;

    public double CellHeight => Bounds.Height / N;

    protected override Size MeasureOverride(Size availableSize)
    {
        _lines.Measure(availableSize);
        _preview.Measure(availableSize);
        foreach (var child in Children)
        {
            if (child is TileFrame { Tile: { } tile })
            {
                child.Measure(new Size(availableSize.Width / N * tile.Width, availableSize.Height / N * tile.Height));
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = finalSize.Width / N;
        var h = finalSize.Height / N;
        _lines.Arrange(new Rect(finalSize));
        if (_previewTile is { } p)
        {
            _preview.Arrange(new Rect(Math.Round(p.X * w), Math.Round(p.Y * h), Math.Round(p.Width * w), Math.Round(p.Height * h)));
        }
        foreach (var child in Children)
        {
            if (child is TileFrame { Tile: { } tile })
            {
                child.Arrange(new Rect(Math.Round(tile.X * w), Math.Round(tile.Y * h), Math.Round(tile.Width * w), Math.Round(tile.Height * h)));
            }
        }

        return finalSize;
    }

    /// <summary>The cell lines while a view is edited.</summary>
    private sealed class GridLines : Control
    {
        public override void Render(DrawingContext context)
        {
            if (!this.TryFindResource("Oadm.BorderBrush", ActualThemeVariant, out var brush) || brush is not IBrush b)
            {
                return;
            }

            var pen = new Pen(b, 1);
            for (var i = 1; i < N; i++)
            {
                var x = Math.Round(i * Bounds.Width / N) + 0.5;
                var y = Math.Round(i * Bounds.Height / N) + 0.5;
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
                context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }
    }

    private void OnTilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    private void OnLayoutChanged(object? sender, EventArgs e) => InvalidateArrange();

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewTab.IsEditing))
        {
            _lines.IsVisible = _tab?.IsEditing == true;
        }
    }

    /// <summary>One frame per tile; frames of tiles that stay keep their picture.</summary>
    private void Sync()
    {
        var tiles = _tab?.Tiles.ToList() ?? [];
        var frames = Children.OfType<TileFrame>().ToDictionary(f => f.Tile!);
        foreach (var (tile, frame) in frames)
        {
            if (!tiles.Contains(tile))
            {
                Children.Remove(frame);
                frame.Tile = null;
            }
        }

        foreach (var tile in tiles.Where(t => !frames.ContainsKey(t)))
        {
            Children.Add(new TileFrame(this) { Tile = tile });
        }

        InvalidateMeasure();
    }
}

/// <summary>
/// One camera in the grid: the live video, or the reason there is none. While editing, a frame over it moves the camera
/// (drag inside), resizes it (drag an edge or a corner, the cursor shows it) and removes it (the X top right); everything
/// snaps to the cells.
/// </summary>
public sealed class TileFrame : Panel
{
    private readonly ViewGrid _grid;
    private readonly ContentControl _video = new();
    private readonly TextBlock _missing = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Border _edit = new();
    private readonly TextBlock _title = new();
    private readonly Button _remove = new();
    private TileViewModel? _tile;
    private Point _start;
    private ViewTile? _startTile;
    private FrameEdges _edges;

    /// <summary>How close to the frame's border a press resizes instead of moving.</summary>
    private const double EdgeBand = 8;

    public TileFrame(ViewGrid grid)
    {
        _grid = grid;
        Classes.Add("viewTile");
        _missing.Classes.Add("secondary");

        _title.Classes.Add("viewTileTitle");
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _remove.Classes.Add("toolbar");
        _remove.Classes.Add("iconOnly");
        _remove.HorizontalAlignment = HorizontalAlignment.Right;
        _remove.VerticalAlignment = VerticalAlignment.Top;
        ToolTip.SetTip(_remove, "Remove camera");
        _remove.Content = new OadmIcon { Width = 12, Height = 12 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(_remove, 1);
        header.Children.AddRange([_title, _remove]);
        _edit.Classes.Add("viewTileEdit");
        _edit.Cursor = new Cursor(StandardCursorType.SizeAll);
        _edit.Child = header;
        _edit.IsVisible = false;

        Children.AddRange([_video, _missing, _edit]);
        _edit.PointerPressed += OnEditPressed;
        _edit.PointerMoved += OnEditMoved;
        _edit.PointerReleased += OnEditReleased;
    }

    public TileViewModel? Tile
    {
        get => _tile;
        set
        {
            if (_tile is not null)
            {
                _tile.PropertyChanged -= OnTileChanged;
                _tile.Owner.PropertyChanged -= OnOwnerChanged;
            }

            _tile = value;
            _video.Content = null;
            if (_tile is not null)
            {
                _tile.PropertyChanged += OnTileChanged;
                _tile.Owner.PropertyChanged += OnOwnerChanged;
                _remove.Command = _tile.RemoveCommand;
            }

            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_remove.Content is OadmIcon icon && this.TryFindResource("Icon.close", ActualThemeVariant, out var x) && x is Geometry g)
        {
            icon.Data = g;
        }
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileViewModel.Video) or nameof(TileViewModel.Device) or nameof(TileViewModel.Title))
        {
            Update();
        }
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewTab.IsEditing))
        {
            Update();
        }
    }

    private void Update()
    {
        var tile = _tile;
        var video = tile?.IsMissing == false ? tile.Video?.View : null;
        if (!ReferenceEquals(_video.Content, video))
        {
            _video.Content = video;
        }

        _missing.Text = tile is null ? null
            : tile.IsMissing ? tile.Title
            : tile.Video is null ? tile.Title
            : null;
        _missing.IsVisible = _missing.Text is not null;
        _title.Text = tile?.Title;
        _edit.IsVisible = tile?.Owner.IsEditing == true;
    }

    private void OnEditPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_tile is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button or OadmIcon)
        {
            return;
        }

        _start = e.GetPosition(_grid);
        _startTile = _tile.ToTile();
        _edges = EdgesAt(e.GetPosition(_edit));
        e.Pointer.Capture(_edit);
        e.Handled = true;
    }

    private void OnEditMoved(object? sender, PointerEventArgs e)
    {
        if (_tile is null || _startTile is not { } start || _grid.CellWidth <= 0 || _grid.CellHeight <= 0)
        {
            _edit.Cursor = Cursors[EdgesAt(e.GetPosition(_edit))]; // the cursor shows what a press would do
            return;
        }

        var delta = e.GetPosition(_grid) - _start;
        var dx = (int)Math.Round(delta.X / _grid.CellWidth);
        var dy = (int)Math.Round(delta.Y / _grid.CellHeight);
        var (x, y, width, height) = ViewLayout.Drag(start, _edges, dx, dy);
        _tile.Owner.TryFit(_tile, x, y, width, height);
    }

    private void OnEditReleased(object? sender, PointerReleasedEventArgs e)
    {
        _startTile = null;
        _edges = FrameEdges.None;
        e.Pointer.Capture(null);
    }

    /// <summary>The edges within <see cref="EdgeBand"/> of <paramref name="point"/> (a corner is two).</summary>
    private FrameEdges EdgesAt(Point point)
    {
        var size = _edit.Bounds.Size;
        var edges = FrameEdges.None;
        if (point.X <= EdgeBand)
        {
            edges |= FrameEdges.Left;
        }
        else if (point.X >= size.Width - EdgeBand)
        {
            edges |= FrameEdges.Right;
        }

        if (point.Y <= EdgeBand)
        {
            edges |= FrameEdges.Top;
        }
        else if (point.Y >= size.Height - EdgeBand)
        {
            edges |= FrameEdges.Bottom;
        }

        return edges;
    }

    private static readonly Dictionary<FrameEdges, Cursor> Cursors = new()
    {
        [FrameEdges.None] = new Cursor(StandardCursorType.SizeAll),
        [FrameEdges.Left] = new Cursor(StandardCursorType.SizeWestEast),
        [FrameEdges.Right] = new Cursor(StandardCursorType.SizeWestEast),
        [FrameEdges.Top] = new Cursor(StandardCursorType.SizeNorthSouth),
        [FrameEdges.Bottom] = new Cursor(StandardCursorType.SizeNorthSouth),
        [FrameEdges.Left | FrameEdges.Top] = new Cursor(StandardCursorType.TopLeftCorner),
        [FrameEdges.Right | FrameEdges.Top] = new Cursor(StandardCursorType.TopRightCorner),
        [FrameEdges.Left | FrameEdges.Bottom] = new Cursor(StandardCursorType.BottomLeftCorner),
        [FrameEdges.Right | FrameEdges.Bottom] = new Cursor(StandardCursorType.BottomRightCorner),
    };
}
