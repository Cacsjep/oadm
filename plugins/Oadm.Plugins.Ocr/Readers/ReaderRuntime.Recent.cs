using System.Collections.Concurrent;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The last reads (docs/spec/ocr.md "The last 20 reads"): per saved reader and per preview the last new values with their
/// cut-outs, server memory only. A value is compared per frame without allocating; a new one adds a row that keeps its frame,
/// and one worker cuts the rows (<see cref="CutAsync"/>), so a frame's loop never waits for a picture and a list holds only
/// the small cut-outs. Only the rows of a list a reply asks for right away are cut on the call.
/// </summary>
internal sealed partial class ReaderRuntime
{
    private readonly ConcurrentDictionary<string, RecentReadList> _recent = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecentReadList> _toCut = new();
    private volatile Task _cutWork = Task.CompletedTask;
    private int _cutting;
    private long _readIds;

    /// <summary>The list of a saved reader, made at its first value.</summary>
    private RecentReadList ReadsOf(string readerId) => _recent.GetOrAdd(readerId, static _ => new RecentReadList());

    /// <summary>The list of a saved reader for tests; null without a value yet.</summary>
    internal RecentReadList? RecentList(string readerId) => _recent.GetValueOrDefault(readerId);

    /// <summary>The rows of a saved reader, newest first (<see cref="RowsOf"/>).</summary>
    public IReadOnlyList<RecentRead> RecentOf(string readerId, bool withCutouts, IReadOnlyList<long>? known) =>
        _recent.TryGetValue(readerId, out var reads) ? RowsOf([reads], withCutouts, known) : [];

    /// <summary>The action's values: a reader may be deleted beside this, its list then goes again.</summary>
    private void AddReads(Reader reader, ReaderReading reading)
    {
        AddReads(ReadsOf(reader.Id), reader, reading);
        if (StoredOf(reader.Id) is null)
        {
            _recent.TryRemove(reader.Id, out _);
        }
    }

    private void AddReads(RecentReadList reads, Reader reader, ReaderReading reading)
    {
        var areas = reading.Reading.Areas;
        for (var i = 0; i < areas.Count; i++)
        {
            AddRead(reads, reader, i, areas[i], SeenIn(reading, i), reading.Reading.Time);
        }
    }

    /// <summary>Where the value of area <paramref name="index"/> of a reading was read: its own frame for a voted value, else this picture.</summary>
    private static ValueFrame SeenIn(ReaderReading reading, int index)
    {
        if (reading.Sources is { } sources && index < sources.Count && !sources[index].Jpeg.IsEmpty)
        {
            return sources[index];
        }

        var details = index < reading.Details.Count ? reading.Details[index] : null;
        return new ValueFrame(reading.Jpeg, details?.MatchBox, details?.MatchTurn);
    }

    /// <summary>Adds a row when the area's value is new; nothing is allocated for a value that did not change.</summary>
    /// <param name="reads">The list.</param>
    /// <param name="reader">The reader as it read the picture.</param>
    /// <param name="index">The area's place in the reader.</param>
    /// <param name="area">The area's result.</param>
    /// <param name="seen">The frame the value was read in and its place there; without a box the area's lines, else the whole area.</param>
    /// <param name="time">When it was taken.</param>
    private void AddRead(RecentReadList reads, Reader reader, int index, AreaResult area, ValueFrame seen, DateTimeOffset time)
    {
        if (!reads.IsNewValue(index, area.Text))
        {
            return;
        }

        var drawn = index < reader.Areas.Count ? reader.Areas[index] : null;
        var hasFormat = drawn?.Expected.Kind == ExpectedTextKind.Format;
        var read = new RecentRead
        {
            Id = Interlocked.Increment(ref _readIds),
            Time = time,
            Area = area.Area,
            Text = TextReadEvents.Rows(!hasFormat && SameText(area.TextRead, area.Text) ? area.TextRead : area.Text),
            Confidence = area.Confidence,
            HasFormat = hasFormat,
            MatchesFormat = area.MatchesFormat,
        };
        var box = seen.Box ?? BoxOf(area.Lines) ?? drawn?.Box ?? RelativeBox.Whole;
        reads.Add(read, _cutter is null || !box.IsValid ? default : seen.Jpeg, box, seen.Turn);
        if (_cutter is not null && reads.TryQueue())
        {
            _toCut.Enqueue(reads);
            StartCutting();
        }
    }

    /// <summary>
    /// True when <paramref name="rows"/> is <paramref name="text"/> in rows: the same characters, spaces and line breaks aside.
    /// A row without a format then shows the rows; where the vote changed a character it shows the value on one line.
    /// </summary>
    internal static bool SameText(string rows, string text)
    {
        var (i, j) = (0, 0);
        while (true)
        {
            while (i < rows.Length && char.IsWhiteSpace(rows[i]))
            {
                i++;
            }

            while (j < text.Length && char.IsWhiteSpace(text[j]))
            {
                j++;
            }

            if (i == rows.Length || j == text.Length)
            {
                return i == rows.Length && j == text.Length && rows.Length > 0;
            }

            if (rows[i++] != text[j++])
            {
                return false;
            }
        }
    }

    /// <summary>The box around <paramref name="lines"/>; null without one.</summary>
    internal static RelativeBox? BoxOf(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var (left, top, right, bottom) = (1.0, 1.0, 0.0, 0.0);
        foreach (var line in lines)
        {
            left = Math.Min(left, line.Box.X);
            top = Math.Min(top, line.Box.Y);
            right = Math.Max(right, line.Box.X + line.Box.Width);
            bottom = Math.Max(bottom, line.Box.Y + line.Box.Height);
        }

        return new RelativeBox(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void StartCutting()
    {
        if (Interlocked.Exchange(ref _cutting, 1) == 0)
        {
            _cutWork = Detached(() => QuietlyAsync(CutAsync, "the cut-out of a read"));
        }
    }

    /// <summary>
    /// The one worker: a row per list in turn, newest first, so one busy reader never holds the others up and at most one
    /// thread cuts pictures. A row that dropped before its turn is never cut.
    /// </summary>
    private Task CutAsync()
    {
        try
        {
            while (!_stopped && _toCut.TryDequeue(out var reads))
            {
                reads.Dequeued();
                if (reads.NextToCut() is { } row)
                {
                    reads.Finish(row, _cutter!.Cut(row.Frame, row.Box, row.Turn));
                    if (reads.NextToCut() is not null && reads.TryQueue())
                    {
                        _toCut.Enqueue(reads);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cutting, 0);
        }

        // A list queued while this worker ended.
        if (!_stopped && !_toCut.IsEmpty)
        {
            StartCutting();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The rows of <paramref name="lists"/>, newest first, at most <see cref="OcrLimits.RecentReads"/>: with the cut-outs when
    /// <paramref name="withCutouts"/>, each once (those in <paramref name="known"/> stay out). Rows still waiting for the
    /// worker are cut here, so a reply never has to come back for them.
    /// </summary>
    private IReadOnlyList<RecentRead> RowsOf(IReadOnlyList<RecentReadList> lists, bool withCutouts, IReadOnlyList<long>? known)
    {
        if (withCutouts && _cutter is { } cutter)
        {
            foreach (var reads in lists)
            {
                while (reads.NextToCut() is { } row)
                {
                    reads.Finish(row, cutter.Cut(row.Frame, row.Box, row.Turn));
                }
            }
        }

        var rows = lists.Count == 1 ? lists[0].Rows() : [.. lists.SelectMany(l => l.Rows()).OrderByDescending(r => r.Id).Take(OcrLimits.RecentReads)];
        return withCutouts && known is null ? rows : [.. rows.Select(r => withCutouts && !known!.Contains(r.Id) ? r : r with { Cutout = null })];
    }

    /// <summary>
    /// What a preview's page shows: the saved reader's list while it shares its stream; a saved On action reader's page also
    /// sees what the action read (rules read it, and only the page's preview reads its stream); else the preview's own.
    /// </summary>
    private IReadOnlyList<RecentReadList> ReadsOf(Preview preview)
    {
        var reader = preview.Reader;
        if (preview.SharedWith is { } shared)
        {
            return [ReadsOf(shared)];
        }

        return reader.Id.Length > 0 && _recent.TryGetValue(reader.Id, out var reads)
            && StoredOf(reader.Id) is { Mode: ReaderMode.OnAction } stored && SamePicture(stored, reader)
            ? [preview.Recent, reads]
            : [preview.Recent];
    }
}
