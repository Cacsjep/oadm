using Oadm.Sdk.Layout;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>
/// Where a camera goes in the 20 x 20 grid of a view: the shared <see cref="TileLayout"/> of
/// <see cref="LiveViewPluginInfo.Layout"/> for the cameras of a view (a dropped camera takes the largest free square at the
/// drop point, up to 10 x 10; moves and resizes stay inside the grid and never overlap).
/// </summary>
public static class ViewLayout
{
    private static TileLayout Layout => LiveViewPluginInfo.Layout;

    /// <summary>The cell for a camera dropped at (<paramref name="cellX"/>, <paramref name="cellY"/>), or null when nothing is free.</summary>
    public static ViewTile? Place(IReadOnlyList<ViewTile> tiles, Guid deviceId, int camera, int cellX, int cellY)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        return Layout.Place(Rects(tiles), cellX, cellY) is { } rect ? ViewTile.At(deviceId, camera, rect) : null;
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
        return Layout.Fit(Rects(others), x, y, width, height) is { } rect ? ViewTile.At(tile.DeviceId, tile.Camera, rect) : null;
    }

    private static TileRect[] Rects(IReadOnlyList<ViewTile> tiles) => [.. tiles.Select(t => t.Rect)];
}
