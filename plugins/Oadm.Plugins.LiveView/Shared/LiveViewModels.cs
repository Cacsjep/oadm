using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.LiveView;

/// <summary>Ids, limits and texts shared by the server part, the page and the tests.</summary>
public static class LiveViewPluginInfo
{
    public const string PluginId = "oadm.live-view";
    public const string DisplayName = "Live View";
    public const string IconKey = "grid";

    /// <summary>The view is a grid of this many columns and rows; it always fills the tab.</summary>
    public const int GridSize = 20;

    /// <summary>Cameras in one view: more would not stay smooth on an average PC.</summary>
    public const int MaxTiles = 16;

    /// <summary>The smallest camera: 2 x 2 cells.</summary>
    public const int MinTileSize = 2;

    /// <summary>A dropped camera takes the largest free square up to this size.</summary>
    public const int DropSize = 10;

    public const int MaxViews = 200;

    public const int MaxNameLength = 64;

    /// <summary>"Change views": create, change and delete views.</summary>
    public const string ManageKey = "manage";

    /// <summary>The event with the whole <see cref="ViewList"/> after every change.</summary>
    public const string ViewsTopic = "views";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One camera of a view: a device's channel on a rectangle of grid cells.</summary>
/// <param name="Camera">1-based channel; 0 = the device's first.</param>
public sealed record ViewTile(Guid DeviceId, int Camera, int X, int Y, int Width, int Height)
{
    public bool Overlaps(ViewTile other) =>
        X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
}

/// <summary>A saved view. <see cref="Version"/> grows with every save; a save of an older version is refused.</summary>
public sealed record SavedView(string Id, string Name, long Version, IReadOnlyList<ViewTile> Tiles);

/// <summary>Every view, sorted by name (method "list" and the event "views").</summary>
public sealed record ViewList(IReadOnlyList<SavedView> Views);

/// <summary>Method "save": <see cref="Id"/> empty creates a view; <see cref="Version"/> is the one the change was made on.</summary>
public sealed record SaveViewRequest(string? Id, string Name, long Version, IReadOnlyList<ViewTile> Tiles);

/// <summary>Method "delete".</summary>
public sealed record DeleteViewRequest(string Id);

/// <summary>The rules of a view, checked by the page while editing and by the server before it stores.</summary>
public static class ViewRules
{
    public const string NameMissing = "Enter a name.";

    public static readonly string NameTooLong = $"Use at most {LiveViewPluginInfo.MaxNameLength} characters.";

    public static readonly string TooManyTiles = $"A view holds at most {LiveViewPluginInfo.MaxTiles} cameras.";

    public const string Outside = "A camera lies outside the view.";

    public const string Overlap = "Two cameras overlap.";

    public const string NoRoom = "There is no free space for another camera. Make a camera smaller or remove one.";

    public const string Gone = "The view no longer exists. Another user deleted it.";

    public static readonly string TooManyViews = $"OADM keeps at most {LiveViewPluginInfo.MaxViews} views. Delete one first.";

    public static string NameTaken(string name) => $"A view named {name} exists already.";

    public static string ChangedMeanwhile(string name) =>
        $"Another user changed the view {name} meanwhile. Click Cancel and make your changes again.";

    /// <summary>The problem of a name, or null.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return NameMissing;
        }

        return trimmed.Length > LiveViewPluginInfo.MaxNameLength || trimmed.Any(char.IsControl) ? NameTooLong : null;
    }

    /// <summary>The problem of a layout, or null: inside the grid, at least 2 x 2, no overlap, at most 16.</summary>
    public static string? TilesProblem(IReadOnlyList<ViewTile>? tiles)
    {
        if (tiles is null)
        {
            return null;
        }

        if (tiles.Count > LiveViewPluginInfo.MaxTiles)
        {
            return TooManyTiles;
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t is null || !Fits(t) || t.DeviceId == Guid.Empty || t.Camera is < 0 or > 64)
            {
                return Outside;
            }

            for (var j = 0; j < i; j++)
            {
                if (t.Overlaps(tiles[j]))
                {
                    return Overlap;
                }
            }
        }

        return null;
    }

    /// <summary>Inside the grid and at least the smallest size.</summary>
    public static bool Fits(ViewTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        const int n = LiveViewPluginInfo.GridSize;
        return tile.X >= 0 && tile.Y >= 0 && tile.Width >= LiveViewPluginInfo.MinTileSize && tile.Height >= LiveViewPluginInfo.MinTileSize
            && tile.X + tile.Width <= n && tile.Y + tile.Height <= n;
    }

    /// <summary>"3 cameras".</summary>
    public static string CameraCount(int count) =>
        count == 1 ? "1 camera" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} cameras");
}
