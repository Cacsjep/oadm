using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>One character of a line with its probability (0 to 1).</summary>
/// <param name="Text">The character.</param>
/// <param name="Probability">How sure the model is, 0 to 1.</param>
public readonly record struct CharProbability(string Text, float Probability);

/// <summary>
/// A line with what <see cref="TextLine"/> does not hold yet: the probability of every character, so the area's confidence is
/// the lowest character probability of its value (docs/spec/ocr.md "Accuracy"), and the format match the recognizer decoded.
/// </summary>
/// <param name="Line">The line as the model types know it; its confidence is the lowest character probability of the line (of the format match when there is one), 0 to 100.</param>
/// <param name="Characters">Every character of <see cref="TextLine.Text"/> in order, with its probability.</param>
/// <param name="FormatMatch">The part of the line that matched the area's format (format-guided decoding); null without a format or match.</param>
public sealed record RecognizedLine(TextLine Line, IReadOnlyList<CharProbability> Characters, string? FormatMatch)
{
    /// <summary>0 to 100: the mean probability of the characters (the spec's drop rule: below 50 the line is left out).</summary>
    public double MeanConfidence => Characters.Count == 0 ? 0 : Characters.Average(c => c.Probability) * 100;

    /// <summary>0 to 100: the lowest probability of the characters.</summary>
    public double MinConfidence => Characters.Count == 0 ? 0 : Characters.Min(c => c.Probability) * 100;

    /// <summary>
    /// 0 to 100: the lowest probability of the characters from <paramref name="start"/> on, <paramref name="length"/> long
    /// (a value's part of the line, as text positions of <see cref="TextLine.Text"/>).
    /// </summary>
    public double MinConfidenceOf(int start, int length)
    {
        var min = 1f;
        var position = 0;
        foreach (var c in Characters)
        {
            var end = position + c.Text.Length;
            if (end > start && position < start + length && c.Text != " ")
            {
                min = Math.Min(min, c.Probability);
            }

            position = end;
        }

        return min * 100;
    }
}

/// <summary>The lines of one area and how many lines were too small to read.</summary>
/// <param name="Lines">The lines, top to bottom, left to right.</param>
/// <param name="TooSmall">Lines left out because the text is lower than <see cref="OnnxTextRecognizer.MinTextHeight"/> pixels.</param>
public sealed record AreaRecognition(IReadOnlyList<RecognizedLine> Lines, int TooSmall)
{
    /// <summary>The page's hint while lines were too small (docs/spec/ocr.md "Accuracy").</summary>
    public string? Hint => TooSmall > 0 && Lines.Count == 0 ? OnnxTextRecognizer.TextTooSmall : null;
}

/// <summary>Everything one picture gave: its size and each area's lines.</summary>
/// <param name="Resolution">The decoded picture's size.</param>
/// <param name="Areas">One entry per area asked for, in order.</param>
public sealed record RecognitionResult(Resolution Resolution, IReadOnlyList<AreaRecognition> Areas)
{
    /// <summary>More than <see cref="Model.OcrLimits.MaxTextBoxes"/> texts were found; only that many (the strongest) were read.</summary>
    public bool TooManyTexts { get; init; }

    /// <summary>The model type of <see cref="ITextRecognizer"/>.</summary>
    public RecognizedPicture ToPicture() => new(Resolution, Areas.Select(a => (IReadOnlyList<TextLine>)a.Lines.Select(l => l.Line).ToList()).ToList())
    {
        TooSmallPerArea = [.. Areas.Select(a => a.Hint is not null)],
        TooManyTexts = TooManyTexts,
    };
}

/// <summary>How one reading runs.</summary>
public sealed record RecognitionOptions
{
    /// <summary>The default: the models of the device that runs (mobile on the CPU, server on a graphics card).</summary>
    public static RecognitionOptions Default { get; } = new();

    /// <summary>The reader's "Accurate": the server models also on the CPU (slower).</summary>
    public bool Accurate { get; init; }

    /// <summary>
    /// Watching: an area whose pixels have barely changed since its last reading gives that reading again without running the
    /// models (<see cref="UnchangedAreas"/>). Never for the action's single reading.
    /// </summary>
    public bool ReuseUnchanged { get; init; }

    /// <summary>Who reads (the reader's id): areas of different readers never share a reading. Only with <see cref="ReuseUnchanged"/>.</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>What runs the recognition: a graphics card by its name, or the CPU with its cores.</summary>
/// <param name="IsGraphicsCard">True on a graphics card (DirectML or CUDA).</param>
/// <param name="Name">"NVIDIA GeForce RTX 5070 Ti" or "CPU (8 cores)".</param>
public sealed record RecognitionDevice(bool IsGraphicsCard, string Name)
{
    /// <summary>The page's settings card line: "Runs on: NVIDIA GeForce RTX 5070 Ti".</summary>
    public string Text => $"Runs on: {Name}";
}

/// <summary>
/// The recognizer with every detail: character probabilities, the format match, the count of too small lines, Accurate, what
/// runs (the accuracy set and the speed measurements). The readers need only <see cref="ITextRecognizer.ReadAsync"/>: its
/// <see cref="TextLine.Confidence"/> is already the lowest character probability and <see cref="RecognizedPicture.TooSmallPerArea"/>
/// the too small areas.
/// </summary>
public interface IDetailedTextRecognizer : ITextRecognizer
{
    /// <summary>What runs the recognition.</summary>
    RecognitionDevice RunsOn { get; }

    /// <summary>Like <see cref="ITextRecognizer.ReadAsync"/>, with every detail.</summary>
    Task<RecognitionResult> ReadDetailedAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, RecognitionOptions options, CancellationToken ct);
}
