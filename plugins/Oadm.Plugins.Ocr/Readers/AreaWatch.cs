using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// One area of a watching reader (docs/spec/ocr.md "Events and the rule action"): the frame vote, then the value is
/// published only once the vote gave it in <see cref="OcrLimits.EqualFramesToPublish"/> frames in a row, and only when it
/// differs from the last published value. An area that becomes empty publishes once with an empty text; an area that is
/// empty from the start publishes nothing. One frame at a time (the reader's loop).
/// </summary>
internal sealed class AreaWatch(IFrameVote vote)
{
    private string? _candidate;
    private int _streak;
    private string _published = string.Empty;

    /// <summary>Adds one frame's result; returns the voted result (with the frame's lines) and whether to publish it.</summary>
    public (AreaResult Voted, bool Publish) Add(AreaResult frame)
    {
        var voted = vote.Add(frame) with { Lines = frame.Lines };
        if (string.Equals(voted.Text, _candidate, StringComparison.Ordinal))
        {
            _streak++;
        }
        else
        {
            _candidate = voted.Text;
            _streak = 1;
        }

        if (_streak < OcrLimits.EqualFramesToPublish || string.Equals(voted.Text, _published, StringComparison.Ordinal))
        {
            return (voted, false);
        }

        _published = voted.Text;
        return (voted, true);
    }
}
