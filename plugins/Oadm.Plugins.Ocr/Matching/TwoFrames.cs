using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// One area of a reader with Moving objects (docs/spec/ocr.md "Moving objects"): no vote; a value counts once it was read in
/// <see cref="OcrLimits.EqualFramesToPublish"/> frames in a row and comes from the frame that confirmed it, so its row, event,
/// cut-out and box all belong to it. A value seen once changes nothing; a value that stays is published once; an area that
/// becomes empty publishes once with an empty text. Keeps no frame but the one of the value it stands for. Not thread-safe:
/// one per area, fed by the reader's one reading at a time.
/// </summary>
public sealed class TwoFrames
{
    private string? _last;
    private int _inARow;

    /// <summary>The value published last; empty before the first.</summary>
    public string Published { get; private set; } = string.Empty;

    /// <summary>The reading that stands for the published value (what the page shows between publications); null before the first.</summary>
    public TextMatch? Current { get; private set; }

    /// <summary>Adds the newest reading of the area and returns what to publish now, or null.</summary>
    public TextMatch? Add(TextMatch reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        _inARow = string.Equals(reading.Text, _last, StringComparison.Ordinal) ? _inARow + 1 : 1;
        _last = reading.Text;
        if (_inARow < OcrLimits.EqualFramesToPublish || string.Equals(reading.Text, Published, StringComparison.Ordinal))
        {
            return null;
        }

        Published = reading.Text;
        Current = reading;
        return reading;
    }
}
