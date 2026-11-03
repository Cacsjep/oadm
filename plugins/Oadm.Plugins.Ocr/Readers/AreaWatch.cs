using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// One area of a watching reader (docs/spec/ocr.md "Events and the rule action", "Accuracy"): each frame's result goes into
/// the area's <see cref="FrameVote"/> (the last 5 frames, character by character, weighted by confidence), which publishes a
/// value only once the vote gave it in <see cref="OcrLimits.EqualFramesToPublish"/> frames in a row and it differs from the
/// last published one; an area that becomes empty publishes once with an empty text. With a format the voted value must
/// match it (and its check digit) like each frame's value; one frame at a time (the reader's loop).
/// </summary>
internal sealed class AreaWatch(ExpectedText expected)
{
    private readonly FrameVote _vote = new(FrameVote.AcceptorOf(expected));

    /// <summary>An area with any text.</summary>
    public AreaWatch()
        : this(ExpectedText.AnyText)
    {
    }

    /// <summary>Adds one frame's result; returns the voted result (with the frame's lines) and whether to publish it.</summary>
    public (AreaResult Voted, bool Publish) Add(AreaResult frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var published = _vote.Add(ToMatch(frame));
        var voted = published ?? _vote.Current ?? ToMatch(frame);
        var result = string.Equals(voted.Text, frame.Text, StringComparison.Ordinal)
            ? frame
            : new AreaResult
            {
                Area = frame.Area,
                Text = voted.Text,
                TextRead = voted.RawText,
                Confidence = voted.Text.Length == 0 ? 0 : voted.Confidence,
                MatchesFormat = voted.MatchesFormat,
                Lines = frame.Lines,
            };
        return (result, published is not null);
    }

    private static TextMatch ToMatch(AreaResult frame) => new()
    {
        Text = frame.Text,
        RawText = frame.TextRead,
        Confidence = frame.Confidence,
        MatchesFormat = frame.MatchesFormat,
        Lines = frame.Lines,
    };
}
