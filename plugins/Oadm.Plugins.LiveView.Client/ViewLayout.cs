namespace Oadm.Plugins.LiveView.Client;

/// <summary>What a drag on a camera's frame changes: no edge moves it, an edge or a corner (two edges) resizes it.</summary>
[Flags]
public enum FrameEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// Where a camera goes in the 20 x 20 grid of a view: a dropped camera takes the largest free square at the drop point
/// (up to 10 x 10), else the largest free square nearest to it; moves and resizes stay inside the grid and never overlap.
/// </summary>
public static class ViewLayout
{
    private const int N = LiveViewPluginInfo.GridSize;

    /// <summary>The cell for a camera dropped at (<paramref name="cellX"/>, <paramref name="cellY"/>), or null when nothing is free.</summary>
    public static ViewTile? Place(IReadOnlyList<ViewTile> tiles, Guid deviceId, int camera, int cellX, int cellY)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count >= LiveViewPluginInfo.MaxTiles)
        {
            return null;
        }

        cellX = Math.Clamp(cellX, 0, N - 1);
        cellY = Math.Clamp(cellY, 0, N - 1);
        var occupied = Occupied(tiles);

        // The largest square that holds the drop point, the one centred best on it.
        for (var size = LiveViewPluginInfo.DropSize; size >= LiveViewPluginInfo.MinTileSize; size--)
        {
            ViewTile? best = null;
            var bestDistance = double.MaxValue;
            for (var y = Math.Max(0, cellY - size + 1); y <= Math.Min(cellY, N - size); y++)
            {
                for (var x = Math.Max(0, cellX - size + 1); x <= Math.Min(cellX, N - size); x++)
                {
                    if (IsFree(occupied, x, y, size, size))
                    {
                        var distance = Distance(x, y, size, cellX, cellY);
                        if (distance < bestDistance)
                        {
                            best = new ViewTile(deviceId, camera, x, y, size, size);
                            bestDistance = distance;
                        }
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        // The drop point is taken: the largest free square anywhere, nearest to it.
        for (var size = LiveViewPluginInfo.DropSize; size >= LiveViewPluginInfo.MinTileSize; size--)
        {
            ViewTile? best = null;
            var bestDistance = double.MaxValue;
            for (var y = 0; y <= N - size; y++)
            {
                for (var x = 0; x <= N - size; x++)
                {
                    if (IsFree(occupied, x, y, size, size))
                    {
                        var distance = Distance(x, y, size, cellX, cellY);
                        if (distance < bestDistance)
                        {
                            best = new ViewTile(deviceId, camera, x, y, size, size);
                            bestDistance = distance;
                        }
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The first free place, top left first (a click on a camera while editing).</summary>
    public static ViewTile? PlaceFirstFree(IReadOnlyList<ViewTile> tiles, Guid deviceId, int camera) => Place(tiles, deviceId, camera, 0, 0);

    /// <summary>
    /// <paramref name="tile"/> moved to (<paramref name="x"/>, <paramref name="y"/>) and sized to <paramref name="width"/> x
    /// <paramref name="height"/>, kept inside the grid and at least 2 x 2; null when it would overlap another camera.
    /// </summary>
    public static ViewTile? Fit(IReadOnlyList<ViewTile> others, ViewTile tile, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(tile);
        width = Math.Clamp(width, LiveViewPluginInfo.MinTileSize, N);
        height = Math.Clamp(height, LiveViewPluginInfo.MinTileSize, N);
        x = Math.Clamp(x, 0, N - width);
        y = Math.Clamp(y, 0, N - height);
        var moved = tile with { X = x, Y = y, Width = width, Height = height };
        return others.Any(o => o.Overlaps(moved)) ? null : moved;
    }

    /// <summary>
    /// The rectangle after dragging <paramref name="edges"/> of <paramref name="start"/> by whole cells (no edge = move):
    /// the opposite edges stay, the camera keeps at least 2 x 2 and stays inside the grid. Overlaps are checked by <see cref="Fit"/>.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Drag(ViewTile start, FrameEdges edges, int dx, int dy)
    {
        ArgumentNullException.ThrowIfNull(start);
        const int min = LiveViewPluginInfo.MinTileSize;
        if (edges == FrameEdges.None)
        {
            return (start.X + dx, start.Y + dy, start.Width, start.Height);
        }

        int left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;
        if (edges.HasFlag(FrameEdges.Left))
        {
            left = Math.Clamp(left + dx, 0, right - min);
        }

        if (edges.HasFlag(FrameEdges.Right))
        {
            right = Math.Clamp(right + dx, left + min, N);
        }

        if (edges.HasFlag(FrameEdges.Top))
        {
            top = Math.Clamp(top + dy, 0, bottom - min);
        }

        if (edges.HasFlag(FrameEdges.Bottom))
        {
            bottom = Math.Clamp(bottom + dy, top + min, N);
        }

        return (left, top, right - left, bottom - top);
    }

    private static bool[,] Occupied(IReadOnlyList<ViewTile> tiles)
    {
        var occupied = new bool[N, N];
        foreach (var t in tiles)
        {
            for (var y = Math.Max(0, t.Y); y < Math.Min(N, t.Y + t.Height); y++)
            {
                for (var x = Math.Max(0, t.X); x < Math.Min(N, t.X + t.Width); x++)
                {
                    occupied[x, y] = true;
                }
            }
        }

        return occupied;
    }

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

    private static double Distance(int x, int y, int size, int cellX, int cellY)
    {
        var dx = x + (size / 2.0) - (cellX + 0.5);
        var dy = y + (size / 2.0) - (cellY + 0.5);
        return (dx * dx) + (dy * dy);
    }
}
