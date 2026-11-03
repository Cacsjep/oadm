using System.Text;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// The vote of one area of a watching reader (docs/spec/ocr.md "Accuracy", "Events and the rule action"): the last
/// <see cref="Window"/> readings are combined character by character, weighted by their confidence, and a value is
/// published only when the vote gives it in <see cref="OcrLimits.EqualFramesToPublish"/> frames in a row and it differs
/// from the value published last. An area that becomes empty publishes once with an empty text; an area that was never
/// read publishes nothing while it stays empty. Not thread-safe: one per area, fed by the reader's one reading at a time.
/// </summary>
public sealed class FrameVote
{
    /// <summary>The vote combines this many last readings.</summary>
    public const int Window = 5;

    /// <summary>A reading's weight in the vote is its confidence, at least this much.</summary>
    private const double MinWeight = 1;

    private readonly LinkedList<TextMatch> _window = new();
    private string? _lastVote;
    private int _inARow;

    /// <summary>The value published last; empty before the first.</summary>
    public string Published { get; private set; } = string.Empty;

    /// <summary>The vote after the last reading; empty before the first.</summary>
    public string Vote => _lastVote ?? string.Empty;

    /// <summary>
    /// Adds the newest reading of the area and returns what to publish now, or null. The result is the newest reading in
    /// the window that read the voted value, else the newest of the voted length with its text replaced by the vote.
    /// </summary>
    public TextMatch? Add(TextMatch reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        _window.AddLast(reading);
        if (_window.Count > Window)
        {
            _window.RemoveFirst();
        }

        var vote = Combine();
        _inARow = vote == _lastVote ? _inARow + 1 : 1;
        _lastVote = vote;
        if (_inARow < OcrLimits.EqualFramesToPublish || vote == Published)
        {
            return null;
        }

        Published = vote;
        return SourceOf(vote);
    }

    /// <summary>Forgets the readings and the published value (the reader was saved anew or its stream restarted from scratch).</summary>
    public void Reset()
    {
        _window.Clear();
        _lastVote = null;
        _inARow = 0;
        Published = string.Empty;
    }

    /// <summary>The value the window votes for.</summary>
    private string Combine()
    {
        var newestFirst = _window.Reverse().ToList();
        var texts = newestFirst.Where(r => r.Text.Length > 0).ToList();
        var empties = newestFirst.Count - texts.Count;
        if (empties > texts.Count || (empties == texts.Count && newestFirst[0].Text.Length == 0))
        {
            return string.Empty;
        }

        // The length with the most weight; on a tie the newest reading's.
        var length = texts
            .GroupBy(r => r.Text.Length)
            .Select(g => (Length: g.Key, Weight: g.Sum(Weight), Newest: texts.IndexOf(g.First())))
            .OrderByDescending(g => g.Weight)
            .ThenBy(g => g.Newest)
            .First().Length;
        var voters = texts.Where(r => r.Text.Length == length).ToList();
        var value = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var position = i;
            value.Append(voters
                .Select((r, order) => (Char: r.Text[position], Weight: Weight(r), Order: order))
                .GroupBy(v => v.Char)
                .Select(g => (Char: g.Key, Weight: g.Sum(v => v.Weight), Newest: g.Min(v => v.Order)))
                .OrderByDescending(g => g.Weight)
                .ThenBy(g => g.Newest)
                .First().Char);
        }

        return value.ToString();
    }

    private TextMatch SourceOf(string vote)
    {
        var newestFirst = _window.Reverse().ToList();
        if (newestFirst.FirstOrDefault(r => string.Equals(r.Text, vote, StringComparison.Ordinal)) is { } same)
        {
            return same;
        }

        var model = newestFirst.First(r => r.Text.Length == vote.Length);
        return model with { Text = vote, Number = TextMatcher.ParseNumber(vote) };
    }

    private static double Weight(TextMatch reading) => Math.Max(MinWeight, reading.Confidence);
}
