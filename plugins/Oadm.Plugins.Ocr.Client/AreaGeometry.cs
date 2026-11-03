using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>What a drag on the selected area changes: the whole area (move) or one corner or edge (resize).</summary>
public enum AreaHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// Moving and resizing an area on the picture (docs/spec/ocr.md "Page"), relative to the picture (0 to 1): an area always
/// stays inside the picture and keeps at least <see cref="MinSize"/> on each side, the same minimum as drawing one.
/// </summary>
public static class AreaGeometry
{
    /// <summary>The smallest area side, relative to the picture (a click is not an area).</summary>
    public const double MinSize = OcrLimits.MinAreaSide;

    /// <summary>One arrow key press moves the selected area by this much; with Shift by <see cref="BigNudge"/>.</summary>
    public const double Nudge = 0.01;

    public const double BigNudge = 0.05;

    /// <summary>The area moved by (<paramref name="dx"/>, <paramref name="dy"/>), held inside the picture with its size kept.</summary>
    public static RelativeBox Move(RelativeBox box, double dx, double dy)
    {
        var width = Math.Clamp(box.Width, MinSize, 1);
        var height = Math.Clamp(box.Height, MinSize, 1);
        return new RelativeBox(Math.Clamp(box.X + dx, 0, 1 - width), Math.Clamp(box.Y + dy, 0, 1 - height), width, height);
    }

    /// <summary>
    /// The area with the edges of <paramref name="handle"/> moved by (<paramref name="dx"/>, <paramref name="dy"/>): each
    /// moved edge stays inside the picture and at least <see cref="MinSize"/> from the opposite edge, which stays in place.
    /// </summary>
    public static RelativeBox Resize(RelativeBox box, AreaHandle handle, double dx, double dy)
    {
        if (handle == AreaHandle.Move)
        {
            return Move(box, dx, dy);
        }

        var left = box.X;
        var top = box.Y;
        var right = box.X + box.Width;
        var bottom = box.Y + box.Height;
        if (handle is AreaHandle.Left or AreaHandle.TopLeft or AreaHandle.BottomLeft)
        {
            left = Math.Clamp(left + dx, 0, right - MinSize);
        }

        if (handle is AreaHandle.Right or AreaHandle.TopRight or AreaHandle.BottomRight)
        {
            right = Math.Clamp(right + dx, left + MinSize, 1);
        }

        if (handle is AreaHandle.Top or AreaHandle.TopLeft or AreaHandle.TopRight)
        {
            top = Math.Clamp(top + dy, 0, bottom - MinSize);
        }

        if (handle is AreaHandle.Bottom or AreaHandle.BottomLeft or AreaHandle.BottomRight)
        {
            bottom = Math.Clamp(bottom + dy, top + MinSize, 1);
        }

        return new RelativeBox(left, top, right - left, bottom - top);
    }

    /// <summary>The rectangle between two points (a drawn area).</summary>
    public static RelativeBox Between(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>A drawn rectangle is an area once both sides reach <see cref="MinSize"/>.</summary>
    public static bool IsBigEnough(RelativeBox box) => box.Width >= MinSize && box.Height >= MinSize;
}
