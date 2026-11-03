using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The vote of one area of a watching reader over its last frames (docs/spec/ocr.md "Accuracy", several frames vote): the
/// last 5 readings combined character by character, weighted by confidence. One instance per area, used by one reading at
/// a time. Package O2 builds the real vote (<c>FrameVote</c>); the readers only need this. Wave 3 wires it in through
/// <see cref="ReaderParts.Vote"/>; without one each frame stands for itself (<see cref="LatestFrameVote"/>).
/// </summary>
public interface IFrameVote
{
    /// <summary>Adds the area's result of one frame and returns the voted result after it (its lines are the frame's).</summary>
    AreaResult Add(AreaResult frame);
}

/// <summary>No vote: the value of the newest frame (until the real vote is wired in, and for tests).</summary>
public sealed class LatestFrameVote : IFrameVote
{
    public AreaResult Add(AreaResult frame) => frame;
}

/// <summary>An area's value with what the events need beyond <see cref="AreaMatch"/>.</summary>
/// <param name="Match">Value, whether it matches and the raw text.</param>
/// <param name="AllMatches">Every different match of the format (at most 10); empty without a format.</param>
/// <param name="Confidence">0 to 100: the lowest character probability of the value; 0 without a value.</param>
/// <param name="MatchBox">Where the value was found, relative to the picture; null without a value.</param>
public sealed record AreaValue(AreaMatch Match, IReadOnlyList<string> AllMatches, double Confidence, RelativeBox? MatchBox);

/// <summary>
/// Optional next to <see cref="ITextMatcher"/> (package O2): a matcher that also gives every different match, the value's
/// own confidence and its box. Without it the readers take the single match, the lowest line confidence and the box around
/// all lines.
/// </summary>
public interface IAreaValueMatcher
{
    /// <summary>The value of an area's <paramref name="lines"/> for <paramref name="expected"/>.</summary>
    AreaValue MatchValue(IReadOnlyList<TextLine> lines, ExpectedText expected);
}

/// <summary>Optional next to <see cref="ITextRecognizer"/> (package O1): what the recognition runs on.</summary>
public interface IRecognizerCapabilities
{
    /// <summary>True while the recognition runs on a graphics card (more watching readers, higher frame rates).</summary>
    bool OnGpu { get; }
}

/// <summary>Cuts the picture of a found value out of a frame (package O1 has the JPEG codec). Optional.</summary>
public interface IPictureCutter
{
    /// <summary>The part <paramref name="box"/> of <paramref name="jpeg"/> as JPEG; null when it cannot be cut.</summary>
    byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box);
}

/// <summary>
/// The parts the readers work with (docs/spec/ocr.md "Build plan": O1 recognition, O2 matching and the vote, O3 frames).
/// The plugin makes them once per start from its context; a part that is missing makes every reading fail with
/// <see cref="ReaderRules.NotInstalled"/> (wave 3 wires the real ones).
/// </summary>
public sealed class ReaderParts
{
    /// <summary>The pictures of the cameras, through the device route.</summary>
    public Func<ICorePluginContext, IFrameSource>? Frames { get; init; }

    /// <summary>The recognition (disposed on stop when it is disposable).</summary>
    public Func<ICorePluginContext, ITextRecognizer>? Recognizer { get; init; }

    /// <summary>The matcher of the expected texts (also <see cref="IAreaValueMatcher"/> when it can).</summary>
    public ITextMatcher? Matcher { get; init; }

    /// <summary>A new vote for one area of a watching reader; null: <see cref="LatestFrameVote"/>.</summary>
    public Func<Area, IFrameVote>? Vote { get; init; }

    /// <summary>Cuts the found values out of the frames for the page; null: the page cuts by <c>MatchBox</c>.</summary>
    public IPictureCutter? Cutter { get; init; }

    /// <summary>The clock of the readings' times.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
