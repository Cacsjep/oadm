using System.Buffers;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// A rotated rectangle: a found line. <see cref="Width"/> runs along <see cref="Angle"/> (radians, -45 to 45 degrees after
/// <see cref="Normalized"/>), <see cref="Height"/> across it.
/// </summary>
internal readonly record struct RotatedBox(float CenterX, float CenterY, float Width, float Height, float Angle, float Score)
{
    /// <summary>The height of the text line: the short side of the enlarged box (0 when not known).</summary>
    public float TextHeight { get; init; }

    /// <summary>The four corners: top left, top right, bottom right, bottom left (along the angle).</summary>
    public (float X, float Y)[] Corners()
    {
        var c = MathF.Cos(Angle);
        var s = MathF.Sin(Angle);
        var hw = Width / 2;
        var hh = Height / 2;
        return
        [
            (CenterX - hw * c + hh * s, CenterY - hw * s - hh * c),
            (CenterX + hw * c + hh * s, CenterY + hw * s - hh * c),
            (CenterX + hw * c - hh * s, CenterY + hw * s + hh * c),
            (CenterX - hw * c - hh * s, CenterY - hw * s + hh * c),
        ];
    }

    /// <summary>The same box with the angle between -45 and 45 degrees (width and height swapped when needed).</summary>
    public RotatedBox Normalized()
    {
        var a = Angle;
        var w = Width;
        var h = Height;
        while (a > MathF.PI / 4)
        {
            a -= MathF.PI / 2;
            (w, h) = (h, w);
        }

        while (a <= -MathF.PI / 4)
        {
            a += MathF.PI / 2;
            (w, h) = (h, w);
        }

        return this with { Width = w, Height = h, Angle = a };
    }

    /// <summary>The box in another frame: scaled by <paramref name="scale"/>, then moved by the offset.</summary>
    public RotatedBox Map(float scale, float offsetX, float offsetY) =>
        this with { CenterX = CenterX * scale + offsetX, CenterY = CenterY * scale + offsetY, Width = Width * scale, Height = Height * scale, TextHeight = TextHeight * scale };

    /// <summary>The upright rectangle around it.</summary>
    public (float Left, float Top, float Right, float Bottom) Bounds()
    {
        var corners = Corners();
        return (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }
}

/// <summary>
/// Turns the text finder's probability map into line boxes (DB post-processing as in PaddleOCR, own C#, no OpenCV): pixels
/// above <see cref="Threshold"/>, connected regions, the smallest rotated rectangle around each, its mean probability as the
/// score (below <see cref="BoxThreshold"/> dropped), the rectangle enlarged by area x <see cref="UnclipRatio"/> / perimeter on
/// every side.
/// </summary>
internal static class DbPostProcess
{
    public const float Threshold = 0.3f;
    public const float BoxThreshold = 0.6f;
    public const float UnclipRatio = 1.5f;

    public const int MaxCandidates = 1000;
    public const float MinSize = 3;

    /// <summary>The line boxes in map pixels.</summary>
    public static List<RotatedBox> Boxes(ReadOnlySpan<float> map, int width, int height, float threshold = Threshold, float boxThreshold = BoxThreshold, float unclipRatio = UnclipRatio)
    {
        // Connected regions (8 neighbours) by a flood fill; per region only the leftmost and rightmost pixel of each row are
        // kept, which is all its convex hull needs.
        var size = width * height;
        var seen = ArrayPool<bool>.Shared.Rent(size);
        var stack = ArrayPool<int>.Shared.Rent(size);
        var rowMin = ArrayPool<int>.Shared.Rent(height);
        var rowMax = ArrayPool<int>.Shared.Rent(height);
        try
        {
            Array.Clear(seen, 0, size);
            for (var y = 0; y < height; y++)
            {
                rowMin[y] = int.MaxValue;
                rowMax[y] = -1;
            }

            var boxes = new List<RotatedBox>();
            for (var start = 0; start < size && boxes.Count < MaxCandidates; start++)
            {
                if (seen[start] || map[start] <= threshold)
                {
                    continue;
                }

                seen[start] = true;
                var top = 0;
                stack[top++] = start;
                var pixels = 0;
                var minY = start / width;
                var maxY = minY;
                while (top > 0)
                {
                    var p = stack[--top];
                    var y = p / width;
                    var x = p - y * width;
                    pixels++;
                    if (x < rowMin[y])
                    {
                        rowMin[y] = x;
                    }

                    if (x > rowMax[y])
                    {
                        rowMax[y] = x;
                    }

                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    var y0 = Math.Max(0, y - 1);
                    var y1 = Math.Min(height - 1, y + 1);
                    var x0 = Math.Max(0, x - 1);
                    var x1 = Math.Min(width - 1, x + 1);
                    for (var ny = y0; ny <= y1; ny++)
                    {
                        var row = ny * width;
                        for (var nx = x0; nx <= x1; nx++)
                        {
                            var q = row + nx;
                            if (!seen[q] && map[q] > threshold)
                            {
                                seen[q] = true;
                                stack[top++] = q;
                            }
                        }
                    }
                }

                var points = new List<(float X, float Y)>((maxY - minY + 1) * 2);
                for (var y = minY; y <= maxY; y++)
                {
                    if (rowMax[y] < 0)
                    {
                        continue;
                    }

                    points.Add((rowMin[y], y));
                    if (rowMax[y] != rowMin[y])
                    {
                        points.Add((rowMax[y], y));
                    }

                    rowMin[y] = int.MaxValue;
                    rowMax[y] = -1;
                }

                var box = Region(map, width, height, points, pixels, boxThreshold, unclipRatio);
                if (box is not null)
                {
                    boxes.Add(box.Value);
                }
            }

            return boxes;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(seen);
            ArrayPool<int>.Shared.Return(stack);
            ArrayPool<int>.Shared.Return(rowMin);
            ArrayPool<int>.Shared.Return(rowMax);
        }
    }

    /// <summary>The box of one region, or null when it is too small or too unsure.</summary>
    private static RotatedBox? Region(ReadOnlySpan<float> map, int width, int height, List<(float X, float Y)> points, int pixels, float boxThreshold, float unclipRatio)
    {
        if (pixels < 4)
        {
            return null;
        }

        var hull = ConvexHull(points);
        if (hull.Count < 3)
        {
            return null;
        }

        var box = MinAreaRect(hull);
        if (Math.Min(box.Width, box.Height) < MinSize)
        {
            return null;
        }

        var score = MeanInside(map, width, height, box);
        if (score < boxThreshold)
        {
            return null;
        }

        var distance = box.Width * box.Height * unclipRatio / (2 * (box.Width + box.Height));
        var grown = box with { Width = box.Width + 2 * distance, Height = box.Height + 2 * distance, Score = score };
        grown = grown with { TextHeight = Math.Min(grown.Width, grown.Height) };
        return Math.Min(grown.Width, grown.Height) < MinSize + 2 ? null : grown.Normalized();
    }

    /// <summary>The convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    public static List<(float X, float Y)> ConvexHull(List<(float X, float Y)> points)
    {
        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var hull = new List<(float X, float Y)>(points.Count + 1);
        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var p in points)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        var lower = hull.Count + 1;
        for (var i = points.Count - 2; i >= 0; i--)
        {
            var p = points[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>The smallest rotated rectangle around a convex hull: one of its sides lies on a hull edge.</summary>
    public static RotatedBox MinAreaRect(List<(float X, float Y)> hull)
    {
        var bestArea = float.MaxValue;
        var best = default(RotatedBox);
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var len = MathF.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6f)
            {
                continue;
            }

            ex /= len;
            ey /= len;
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var p in hull)
            {
                var u = p.X * ex + p.Y * ey;
                var v = -p.X * ey + p.Y * ex;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            var area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                var cu = (minU + maxU) / 2;
                var cv = (minV + maxV) / 2;
                best = new RotatedBox(cu * ex - cv * ey, cu * ey + cv * ex, maxU - minU, maxV - minV, MathF.Atan2(ey, ex), 0);
            }
        }

        return best.Normalized();
    }

    /// <summary>The mean probability inside the box (pixel centres inside, at least the box's own pixels).</summary>
    private static float MeanInside(ReadOnlySpan<float> map, int width, int height, RotatedBox box)
    {
        var (left, top, right, bottom) = box.Bounds();
        var x0 = Math.Max(0, (int)MathF.Floor(left));
        var y0 = Math.Max(0, (int)MathF.Floor(top));
        var x1 = Math.Min(width - 1, (int)MathF.Ceiling(right));
        var y1 = Math.Min(height - 1, (int)MathF.Ceiling(bottom));
        var c = MathF.Cos(box.Angle);
        var s = MathF.Sin(box.Angle);
        var hw = box.Width / 2 + 0.5f;
        var hh = box.Height / 2 + 0.5f;
        double sum = 0;
        var n = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - box.CenterX;
                var dy = y - box.CenterY;
                var u = dx * c + dy * s;
                var v = -dx * s + dy * c;
                if (MathF.Abs(u) <= hw && MathF.Abs(v) <= hh)
                {
                    sum += map[y * width + x];
                    n++;
                }
            }
        }

        return n == 0 ? 0 : (float)(sum / n);
    }
}
