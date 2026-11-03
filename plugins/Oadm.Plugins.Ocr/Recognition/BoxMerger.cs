namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// Merges the boxes of overlapping tiles (docs/spec/ocr.md "Reading" step 2): a line found in two tiles, whole or cut at a
/// tile's edge, becomes one box. Two boxes merge when they run in the same direction, have about the same height, lie on
/// the same line and overlap along it or are closer than <see cref="MaxGap"/> line heights: pieces of one line the text
/// finder split (the digit drums of a meter) are read as one line.
/// </summary>
internal static class BoxMerger
{
    private const float MaxAngleDifference = 10 * MathF.PI / 180;

    /// <summary>Pieces of one line closer than this many line heights are one line.</summary>
    public const float MaxGap = 0.6f;

    /// <summary>The boxes with every duplicate merged.</summary>
    public static List<RotatedBox> Merge(IEnumerable<RotatedBox> boxes)
    {
        var list = boxes.ToList();
        var merged = true;
        while (merged)
        {
            // A grown box may now reach one it did not before: passes until nothing merges.
            merged = false;
            for (var i = 0; i < list.Count; i++)
            {
                var j = i + 1;
                while (j < list.Count)
                {
                    if (SameLine(list[i], list[j]))
                    {
                        list[i] = Union(list[i], list[j]);
                        list.RemoveAt(j);
                        merged = true;
                        j = i + 1;
                    }
                    else
                    {
                        j++;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are parts of one line: the line runs along the long side of
    /// the longer box (a receipt turned by 90 degrees has upright lines).
    /// </summary>
    public static bool SameLine(RotatedBox a, RotatedBox b)
    {
        var (dirA, thickA, ratioA) = Line(a);
        var (dirB, thickB, ratioB) = Line(b);
        if (ratioA >= 1.3f && ratioB >= 1.3f && AngleBetween(dirA, dirB) > MaxAngleDifference)
        {
            return false;
        }

        var hMin = Math.Min(thickA, thickB);
        var hMax = Math.Max(thickA, thickB);
        if (hMax > 1.6f * hMin)
        {
            return false;
        }

        // Along the line: the two overlap or nearly touch; across it: they share most of their height.
        var direction = ratioA >= ratioB ? dirA : dirB;
        var c = MathF.Cos(direction);
        var s = MathF.Sin(direction);
        var (aU0, aU1, aV0, aV1) = Project(a, c, s);
        var (bU0, bU1, bV0, bV1) = Project(b, c, s);
        var along = Math.Min(aU1, bU1) - Math.Max(aU0, bU0);
        var across = Math.Min(aV1, bV1) - Math.Max(aV0, bV0);
        return along > -MaxGap * hMax && across > 0.6f * hMin;
    }

    /// <summary>
    /// A box's line direction, its thickness and how elongated it is: along the box's angle (within 45 degrees of level),
    /// upright only when the box is clearly taller than wide (a single character stays level).
    /// </summary>
    private static (float Direction, float Thickness, float Ratio) Line(RotatedBox box) =>
        box.Height >= 1.3f * box.Width
            ? (box.Angle + MathF.PI / 2, box.Width, box.Height / Math.Max(1e-3f, box.Width))
            : (box.Angle, box.Height, box.Width / Math.Max(1e-3f, box.Height));

    /// <summary>The angle between two line directions (a line has no front: 0 to 90 degrees).</summary>
    private static float AngleBetween(float a, float b)
    {
        var d = MathF.Abs(a - b) % MathF.PI;
        return Math.Min(d, MathF.PI - d);
    }

    private static (float U0, float U1, float V0, float V1) Project(RotatedBox box, float c, float s)
    {
        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        foreach (var (x, y) in box.Corners())
        {
            var u = x * c + y * s;
            var v = -x * s + y * c;
            u0 = Math.Min(u0, u);
            u1 = Math.Max(u1, u);
            v0 = Math.Min(v0, v);
            v1 = Math.Max(v1, v);
        }

        return (u0, u1, v0, v1);
    }

    private static RotatedBox Union(RotatedBox a, RotatedBox b)
    {
        var points = a.Corners().Concat(b.Corners()).ToList();
        var box = DbPostProcess.MinAreaRect(DbPostProcess.ConvexHull(points));
        return box with { Score = Math.Max(a.Score, b.Score), TextHeight = Math.Max(a.TextHeight, b.TextHeight) };
    }
}
