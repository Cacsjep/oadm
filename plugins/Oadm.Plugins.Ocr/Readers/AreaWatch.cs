using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>Where a value was read: its frame and its place there, as far as they are known.</summary>
/// <param name="Jpeg">The frame; empty when the value has none.</param>
/// <param name="Box">The value's box in the frame.</param>
/// <param name="Turn">How the value's line lies in the frame; null: the cut-out stays level.</param>
public readonly record struct ValueFrame(ReadOnlyMemory<byte> Jpeg, RelativeBox? Box, LineTurn? Turn);

/// <summary>What an area makes of one frame.</summary>
/// <param name="Voted">The value that stands now, with the frame's lines.</param>
/// <param name="Publish">The value is new: its row and event follow.</param>
/// <param name="Seen">Where <paramref name="Voted"/> was read.</param>
internal readonly record struct AreaDecision(AreaResult Voted, bool Publish, ValueFrame Seen);

/// <summary>
/// One area of a watching reader (docs/spec/ocr.md "Events and the rule action", "Accuracy"): each frame's result goes into
/// the area's <see cref="FrameVote"/> (the last 5 frames, character by character, weighted by confidence), which publishes a
/// value only once the vote gave it in <see cref="OcrLimits.EqualFramesToPublish"/> frames in a row and it differs from the
/// last published one; an area that becomes empty publishes once with an empty text. With a format the voted value must
/// match it (and its check digit) like each frame's value. With Moving objects <see cref="TwoFrames"/> takes the vote's
/// place. Each candidate keeps a reference to the frame it was read in, released as the window moves on, so a value's
/// cut-out always comes from that frame. One frame at a time (the reader's loop).
/// </summary>
internal sealed class AreaWatch(ExpectedText expected, bool moving = false)
{
    private readonly FrameVote? _vote = moving ? null : new FrameVote(FrameVote.AcceptorOf(expected));
    private readonly TwoFrames? _two = moving ? new TwoFrames() : null;

    /// <summary>An area with any text.</summary>
    public AreaWatch()
        : this(ExpectedText.AnyText)
    {
    }

    /// <summary>The frames this area holds now: at most <see cref="FrameVote.Window"/>, none with Moving objects but its value's.</summary>
    public int HeldFrames => _vote?.HeldFrames ?? (_two!.Current is { Frame.IsEmpty: false } ? 1 : 0);

    /// <summary>Adds one frame's result; <paramref name="seen"/> is that frame and the box and turn of its value.</summary>
    public AreaDecision Add(AreaResult frame, ValueFrame seen = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var match = ToMatch(frame, seen);
        var published = _vote is not null ? _vote.Add(match) : _two!.Add(match);
        var voted = published ?? (_vote is not null ? _vote.Current ?? match : _two!.Current ?? TextMatch.Empty);
        var result = string.Equals(voted.Text, frame.Text, StringComparison.Ordinal)
            ? frame
            : new AreaResult
            {
                Area = frame.Area,
                Text = voted.Text,
                TextRead = _two is null ? voted.RawText : frame.TextRead,
                Confidence = voted.Text.Length == 0 ? 0 : voted.Confidence,
                MatchesFormat = voted.MatchesFormat,
                Lines = frame.Lines,
            };
        return new AreaDecision(result, published is not null, new ValueFrame(voted.Frame, voted.TextBox ?? ReaderRuntime.BoxOf(voted.Lines), voted.Turn));
    }

    private static TextMatch ToMatch(AreaResult frame, ValueFrame seen) => new()
    {
        Text = frame.Text,
        RawText = frame.TextRead,
        Confidence = frame.Confidence,
        MatchesFormat = frame.MatchesFormat,
        Lines = frame.Lines,
        TextBox = seen.Box,
        Turn = seen.Turn,
        Frame = frame.Text.Length > 0 ? seen.Jpeg : default, // an empty value needs no picture
    };
}
