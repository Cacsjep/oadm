using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The last reads of one reader or one preview (docs/spec/ocr.md "The last 20 reads"): at most
/// <see cref="OcrLimits.RecentReads"/> rows, the oldest drops first, each with its cut-out, in server memory only. A row
/// holds its frame only until its cut-out is made, so the list holds no more than the rows' small pictures. Every member may
/// run beside the others.
/// </summary>
internal sealed class RecentReadList
{
    private readonly object _lock = new();
    private readonly List<Row> _rows = new(OcrLimits.RecentReads + 1); // oldest first
    private readonly string?[] _values = new string?[OcrLimits.MaxAreas];
    private int _queued;

    /// <summary>The rows now (tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _rows.Count;
            }
        }
    }

    /// <summary>The bytes of the pictures the rows hold now, frames not yet cut included (tests).</summary>
    public long PictureBytes
    {
        get
        {
            lock (_lock)
            {
                return _rows.Sum(r => (long)(r.Read.Cutout?.Length ?? 0) + r.Frame.Length);
            }
        }
    }

    /// <summary>
    /// The area at place <paramref name="area"/> now reads <paramref name="value"/>: true when it differs from its last value
    /// and is not empty. Allocates nothing: this runs per frame for a preview.
    /// </summary>
    public bool IsNewValue(int area, string value)
    {
        if (area >= _values.Length)
        {
            return false;
        }

        lock (_lock)
        {
            if (string.Equals(_values[area] ?? string.Empty, value, StringComparison.Ordinal))
            {
                return false;
            }

            _values[area] = value;
            return value.Length > 0;
        }
    }

    /// <summary>The next value of each area is a new one, whatever it reads (the reader's picture changed).</summary>
    public void ForgetValues()
    {
        lock (_lock)
        {
            Array.Clear(_values);
        }
    }

    /// <summary>Adds a row, the oldest drops; <paramref name="frame"/> (empty: no cut-out comes) is kept until <see cref="Finish"/>.</summary>
    public void Add(RecentRead read, ReadOnlyMemory<byte> frame, RelativeBox box, LineTurn? turn = null)
    {
        lock (_lock)
        {
            _rows.Add(new Row(read, frame, box, turn));
            if (_rows.Count > OcrLimits.RecentReads)
            {
                _rows.RemoveAt(0);
            }
        }
    }

    /// <summary>The newest row still without its cut-out: an older one may drop before its turn.</summary>
    public Row? NextToCut()
    {
        lock (_lock)
        {
            for (var i = _rows.Count - 1; i >= 0; i--)
            {
                if (!_rows[i].Frame.IsEmpty)
                {
                    return _rows[i];
                }
            }

            return null;
        }
    }

    /// <summary>The row's cut-out is made (null: none can be made): its frame goes; one over <see cref="OcrLimits.MaxCutoutBytes"/> is not kept.</summary>
    public void Finish(Row row, byte[]? cutout)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            if (!row.Frame.IsEmpty)
            {
                row.Frame = default;
                row.Read = row.Read with { Cutout = cutout is { Length: > 0 and <= OcrLimits.MaxCutoutBytes } ? cutout : null };
            }
        }
    }

    /// <summary>True for the first asker until <see cref="Dequeued"/>: a list waits for the cutter once.</summary>
    public bool TryQueue() => Interlocked.Exchange(ref _queued, 1) == 0;

    /// <summary>The cutter took the list from its queue.</summary>
    public void Dequeued() => Volatile.Write(ref _queued, 0);

    /// <summary>The rows, newest first.</summary>
    public IReadOnlyList<RecentRead> Rows()
    {
        lock (_lock)
        {
            var rows = new RecentRead[_rows.Count];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = _rows[_rows.Count - 1 - i].Read;
            }

            return rows;
        }
    }

    /// <summary>A row and, until it is cut, its frame and where the value is.</summary>
    internal sealed class Row(RecentRead read, ReadOnlyMemory<byte> frame, RelativeBox box, LineTurn? turn)
    {
        public RecentRead Read { get; set; } = read;

        public ReadOnlyMemory<byte> Frame { get; set; } = frame;

        public RelativeBox Box { get; } = box;

        public LineTurn? Turn { get; } = turn;
    }
}
