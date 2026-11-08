using Avalonia;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// Moving and resizing an area on the picture (docs/spec/ocr.md "Page"), relative to the picture (0 to 1): the rectangle
/// case of the shared shape rules (<see cref="ShapeGeometry"/>, plugins/Shared/Vision.Client). An area always stays inside the
/// picture and keeps at least <see cref="MinSize"/> on each side, the same minimum as drawing one.
/// </summary>
public static class AreaGeometry
{
    /// <summary>The smallest area side, relative to the picture (a click is not an area).</summary>
    public const double MinSize = OcrLimits.MinAreaSide;

    /// <summary>One arrow key press moves the selected area by this much; with Shift by <see cref="BigNudge"/>.</summary>
    public const double Nudge = ShapeGeometry.Nudge;

    public const double BigNudge = ShapeGeometry.BigNudge;

    /// <summary>The area moved by (<paramref name="dx"/>, <paramref name="dy"/>), held inside the picture with its size kept.</summary>
    public static RelativeBox Move(RelativeBox box, double dx, double dy) => Of(ShapeGeometry.MoveBox(ToRect(box), dx, dy, MinSize));

    /// <summary>
    /// The area with the edges of <paramref name="handle"/> moved by (<paramref name="dx"/>, <paramref name="dy"/>): each
    /// moved edge stays inside the picture and at least <see cref="MinSize"/> from the opposite edge, which stays in place.
    /// </summary>
    public static RelativeBox Resize(RelativeBox box, AreaHandle handle, double dx, double dy) =>
        Of(ShapeGeometry.ResizeBox(ToRect(box), handle, dx, dy, MinSize));

    /// <summary>The rectangle between two points (a drawn area).</summary>
    public static RelativeBox Between(double x1, double y1, double x2, double y2) => Of(ShapeGeometry.Between(x1, y1, x2, y2));

    /// <summary>A drawn rectangle is an area once both sides reach <see cref="MinSize"/>.</summary>
    public static bool IsBigEnough(RelativeBox box) => ShapeGeometry.IsBigEnough(ToRect(box), MinSize);

    internal static Rect ToRect(RelativeBox box) => new(box.X, box.Y, box.Width, box.Height);

    private static RelativeBox Of(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
