namespace Oadm.Sdk.Layout;

/// <summary>A rectangle of grid cells: the place of one tile (a camera of a Live View view, a widget of a dashboard).</summary>
/// <param name="X">The left column, 0-based.</param>
/// <param name="Y">The top row, 0-based.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
public readonly record struct TileRect(int X, int Y, int Width, int Height)
{
    /// <summary>Whether the two rectangles share a cell.</summary>
    public bool Overlaps(TileRect other) =>
        X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
}

/// <summary>What a drag on a tile's frame changes: no edge moves it, an edge or a corner (two edges) resizes it.</summary>
[Flags]
public enum TileEdges
{
    /// <summary>A drag inside the frame: the tile moves.</summary>
    None = 0,
    /// <summary>The left edge.</summary>
    Left = 1,
    /// <summary>The top edge.</summary>
    Top = 2,
    /// <summary>The right edge.</summary>
    Right = 4,
    /// <summary>The bottom edge.</summary>
    Bottom = 8,
}

/// <summary>
/// The rules of a tile grid (<see cref="Size"/> x <see cref="Size"/> cells that always fill the page): tiles stay inside,
/// are at least <see cref="MinTileSize"/> cells wide and high, never overlap, and there are at most <see cref="MaxTiles"/>.
/// A dropped tile takes the largest free square at the drop point (up to <see cref="DropSize"/>), else the largest free
/// square nearest to it. Used by the page while editing and by the server part before it stores (no UI here).
/// </summary>
public sealed class TileLayout
{
    /// <summary>A layout with the given rules (Live View: 20 cells, 2 x 2 at least, drops up to 10 x 10, 16 tiles).</summary>
    public TileLayout(int size = 20, int minTileSize = 2, int dropSize = 10, int maxTiles = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minTileSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minTileSize, size);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTiles, 1);
        Size = size;
        MinTileSize = minTileSize;
        DropSize = Math.Clamp(dropSize, minTileSize, size);
        MaxTiles = maxTiles;
    }

    /// <summary>Columns and rows of the grid.</summary>
    public int Size { get; }

    /// <summary>The smallest tile, in cells per side.</summary>
    public int MinTileSize { get; }

    /// <summary>The largest square a dropped tile takes.</summary>
    public int DropSize { get; }

    /// <summary>Tiles in one grid at most.</summary>
    public int MaxTiles { get; }

    /// <summary>Inside the grid and at least the smallest size.</summary>
    public bool Fits(TileRect tile) =>
        tile.X >= 0 && tile.Y >= 0 && tile.Width >= MinTileSize && tile.Height >= MinTileSize
        && tile.X <= Size && tile.Y <= Size && tile.Width <= Size - tile.X && tile.Height <= Size - tile.Y; // no overflow for huge values

    /// <summary>The index of the first tile that overlaps an earlier one, or -1.</summary>
    public static int FirstOverlap(IReadOnlyList<TileRect> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        for (var i = 1; i < tiles.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (tiles[i].Overlaps(tiles[j]))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>The place of a tile dropped at (<paramref name="cellX"/>, <paramref name="cellY"/>), or null when nothing is free.</summary>
    public TileRect? Place(IReadOnlyList<TileRect> tiles, int cellX, int cellY)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count >= MaxTiles)
        {
            return null;
        }

        cellX = Math.Clamp(cellX, 0, Size - 1);
        cellY = Math.Clamp(cellY, 0, Size - 1);
        var occupied = Occupied(tiles);

        // The largest square that holds the drop point, the one centred best on it.
        for (var size = DropSize; size >= MinTileSize; size--)
        {
            if (Best(occupied, size, Math.Max(0, cellX - size + 1), Math.Min(cellX, Size - size), Math.Max(0, cellY - size + 1), Math.Min(cellY, Size - size), cellX, cellY) is { } best)
            {
                return best;
            }
        }

        // The drop point is taken: the largest free square anywhere, nearest to it.
        for (var size = DropSize; size >= MinTileSize; size--)
        {
            if (Best(occupied, size, 0, Size - size, 0, Size - size, cellX, cellY) is { } best)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The first free place, top left first (a click on an item while editing).</summary>
    public TileRect? PlaceFirstFree(IReadOnlyList<TileRect> tiles) => Place(tiles, 0, 0);

    /// <summary>
    /// The first free place for a tile of <paramref name="width"/> x <paramref name="height"/> (row by row, top left first),
    /// e.g. a copy of a tile; null when the grid is full or no such place is free.
    /// </summary>
    public TileRect? PlaceSized(IReadOnlyList<TileRect> tiles, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count >= MaxTiles)
        {
            return null;
        }

        width = Math.Clamp(width, MinTileSize, Size);
        height = Math.Clamp(height, MinTileSize, Size);
        var occupied = Occupied(tiles);
        for (var y = 0; y <= Size - height; y++)
        {
            for (var x = 0; x <= Size - width; x++)
            {
                if (IsFree(occupied, x, y, width, height))
                {
                    return new TileRect(x, y, width, height);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A tile moved to (<paramref name="x"/>, <paramref name="y"/>) and sized to <paramref name="width"/> x
    /// <paramref name="height"/>, kept inside the grid and at least the smallest size; null when it would overlap one of
    /// <paramref name="others"/>.
    /// </summary>
    public TileRect? Fit(IReadOnlyList<TileRect> others, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(others);
        width = Math.Clamp(width, MinTileSize, Size);
        height = Math.Clamp(height, MinTileSize, Size);
        var moved = new TileRect(Math.Clamp(x, 0, Size - width), Math.Clamp(y, 0, Size - height), width, height);
        return others.Any(o => o.Overlaps(moved)) ? null : moved;
    }

    /// <summary>
    /// The rectangle after dragging <paramref name="edges"/> of <paramref name="start"/> by whole cells (no edge = move):
    /// the opposite edges stay, the tile keeps the smallest size and stays inside the grid. Overlaps are checked by
    /// <see cref="Fit"/>.
    /// </summary>
    public TileRect Drag(TileRect start, TileEdges edges, int dx, int dy)
    {
        if (edges == TileEdges.None)
        {
            return start with { X = start.X + dx, Y = start.Y + dy };
        }

        int left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;
        if (edges.HasFlag(TileEdges.Left))
        {
            left = Math.Clamp(left + dx, 0, right - MinTileSize);
        }

        if (edges.HasFlag(TileEdges.Right))
        {
            right = Math.Clamp(right + dx, left + MinTileSize, Size);
        }

        if (edges.HasFlag(TileEdges.Top))
        {
            top = Math.Clamp(top + dy, 0, bottom - MinTileSize);
        }

        if (edges.HasFlag(TileEdges.Bottom))
        {
            bottom = Math.Clamp(bottom + dy, top + MinTileSize, Size);
        }

        return new TileRect(left, top, right - left, bottom - top);
    }

    private static TileRect? Best(bool[,] occupied, int size, int x0, int x1, int y0, int y1, int cellX, int cellY)
    {
        TileRect? best = null;
        var bestDistance = double.MaxValue;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (IsFree(occupied, x, y, size))
                {
                    var dx = x + (size / 2.0) - (cellX + 0.5);
                    var dy = y + (size / 2.0) - (cellY + 0.5);
                    var distance = (dx * dx) + (dy * dy);
                    if (distance < bestDistance)
                    {
                        best = new TileRect(x, y, size, size);
                        bestDistance = distance;
                    }
                }
            }
        }

        return best;
    }

    private bool[,] Occupied(IReadOnlyList<TileRect> tiles)
    {
        var occupied = new bool[Size, Size];
        foreach (var t in tiles)
        {
            for (var y = Math.Max(0, t.Y); y < Math.Min(Size, t.Y + t.Height); y++)
            {
                for (var x = Math.Max(0, t.X); x < Math.Min(Size, t.X + t.Width); x++)
                {
                    occupied[x, y] = true;
                }
            }
        }

        return occupied;
    }

    private static bool IsFree(bool[,] occupied, int x0, int y0, int size) => IsFree(occupied, x0, y0, size, size);

    private static bool IsFree(bool[,] occupied, int x0, int y0, int width, int height)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                if (occupied[x, y])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
