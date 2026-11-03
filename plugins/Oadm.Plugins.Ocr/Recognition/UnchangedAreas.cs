using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The last reading of each area with a coarse print of its pixels (docs/spec/ocr.md "Reading", unchanged areas): when an
/// area's pixels have barely changed since its last real reading, that reading is used again instead of running the models.
/// The print is the mean brightness of cells of about <see cref="CellPixels"/> pixels; the area counts as unchanged while no
/// cell moved by more than <see cref="MaxCellChange"/> levels (a changed digit of a meter moves its cells by tens of levels,
/// camera noise by about one) and the mean change stays below <see cref="MaxMeanChange"/>. After <see cref="MaxReuses"/> uses
/// in a row the area is read again anyway. Compared with the print of the last real reading, so a slow drift (the light of
/// the day) also ends in a new reading. Prints and readings live in memory only, at most <see cref="MaxEntries"/>.
/// </summary>
internal sealed class UnchangedAreas
{
    /// <summary>The side of a print cell in picture pixels.</summary>
    public const int CellPixels = 8;

    /// <summary>The most cells along a side.</summary>
    public const int MaxCells = 48;

    /// <summary>The largest change of one cell (0 to 255) that still counts as unchanged.</summary>
    public const float MaxCellChange = 6;

    /// <summary>The largest mean change of the cells that still counts as unchanged.</summary>
    public const float MaxMeanChange = 1.5f;

    /// <summary>Uses of one reading in a row at most before the area is read again.</summary>
    public const int MaxReuses = 10;

    /// <summary>Areas kept at most (the least recently used goes).</summary>
    public const int MaxEntries = 512;

    private readonly object _lock = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private long _clock;

    /// <summary>Area readings used again so far (tests).</summary>
    public int Reused { get; private set; }

    /// <summary>The key of an area: who reads it, where it is, what it expects and how it is read.</summary>
    public readonly record struct Key(string Source, SKRectI Rect, int Width, int Height, ExpectedTextKind Kind, string? Format, bool Accurate, bool OnGpu);

    /// <summary>The print of the area <paramref name="rect"/> of <paramref name="picture"/>.</summary>
    public static float[] Print(Picture picture, SKRectI rect) => picture.CellMeans(rect, Cells(rect.Width), Cells(rect.Height));

    /// <summary>The area's last reading when its pixels have barely changed (<paramref name="print"/>), else null.</summary>
    public AreaRecognition? TryReuse(Key key, float[] print)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Uses >= MaxReuses || !Unchanged(entry.Print, print))
            {
                return null;
            }

            entry.Uses++;
            entry.Used = ++_clock;
            Reused++;
            return entry.Result;
        }
    }

    /// <summary>Keeps a real reading of the area with its print.</summary>
    public void Keep(Key key, float[] print, AreaRecognition result)
    {
        lock (_lock)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= MaxEntries)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.Used).Key);
            }

            _entries[key] = new Entry(print, result) { Used = ++_clock };
        }
    }

    /// <summary>True when no cell changed by more than <see cref="MaxCellChange"/> and the mean change is small.</summary>
    internal static bool Unchanged(float[] before, float[] now)
    {
        if (before.Length != now.Length || now.Length == 0)
        {
            return false;
        }

        var sum = 0f;
        for (var i = 0; i < now.Length; i++)
        {
            var change = MathF.Abs(now[i] - before[i]);
            if (change > MaxCellChange)
            {
                return false;
            }

            sum += change;
        }

        return sum / now.Length <= MaxMeanChange;
    }

    private static int Cells(int pixels) => Math.Clamp((pixels + CellPixels - 1) / CellPixels, 1, MaxCells);

    private sealed class Entry(float[] print, AreaRecognition result)
    {
        public float[] Print { get; } = print;

        public AreaRecognition Result { get; } = result;

        public int Uses { get; set; }

        public long Used { get; set; }
    }
}
