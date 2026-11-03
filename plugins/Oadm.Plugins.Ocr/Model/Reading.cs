namespace Oadm.Plugins.Ocr.Model;

/// <summary>A line of text found in a picture.</summary>
/// <param name="Text">The text read.</param>
/// <param name="Box">Where it is, relative to the whole picture (for the page's drawing).</param>
/// <param name="Confidence">
/// 0 to 100, the lowest probability of its characters (of the format match when the area has a format and the line holds a
/// match), so an area's confidence is the lowest character probability of its value. Lines whose characters' mean
/// probability is below <see cref="OcrLimits.MinConfidence"/> are dropped.
/// </param>
public sealed record TextLine(string Text, RelativeBox Box, double Confidence);

/// <summary>What <see cref="ITextMatcher"/> makes of an area's lines.</summary>
/// <param name="Value">The area's value: the match with a format (empty without one), else all lines joined with line breaks.</param>
/// <param name="MatchesFormat">True when a format matched; true for Any text with text, false for an empty area.</param>
/// <param name="RawText">All lines, top to bottom and left to right, joined with line breaks.</param>
public sealed record AreaMatch(string Value, bool MatchesFormat, string RawText);

/// <summary>The result of one area in one reading (the page's results row, one event "Text read").</summary>
public sealed record AreaResult
{
    /// <summary>The area's name.</summary>
    public string Area { get; init; } = string.Empty;

    /// <summary>The value: the match with a format, else all lines.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>All lines, raw.</summary>
    public string TextRead { get; init; } = string.Empty;

    /// <summary>0 to 100: the lowest confidence of the lines that make the value; 0 without text.</summary>
    public double Confidence { get; init; }

    /// <summary>Whether the format matched (Any text: there is text).</summary>
    public bool MatchesFormat { get; init; }

    /// <summary>The lines found in the area, with their boxes.</summary>
    public IReadOnlyList<TextLine> Lines { get; init; } = [];
}

/// <summary>One reading of a reader: one picture, every area's result. Kept in server memory only (the last per reader).</summary>
public sealed record Reading
{
    /// <summary>The reader.</summary>
    public string ReaderId { get; init; } = string.Empty;

    /// <summary>When the picture was taken (the plugin's TimeProvider).</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>The picture's size.</summary>
    public Resolution Resolution { get; init; }

    /// <summary>Every area's result, in the reader's area order.</summary>
    public IReadOnlyList<AreaResult> Areas { get; init; } = [];

    /// <summary>
    /// A note for the page about this reading (<see cref="Readers.ReaderRules.TooManyTexts"/> when more than
    /// <see cref="OcrLimits.MaxTextBoxes"/> texts were found); null without one.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>What <see cref="ITextRecognizer"/> found in one picture.</summary>
/// <param name="Resolution">The decoded picture's size.</param>
/// <param name="LinesPerArea">The lines of each area, in the order of the areas asked for.</param>
public sealed record RecognizedPicture(Resolution Resolution, IReadOnlyList<IReadOnlyList<TextLine>> LinesPerArea)
{
    /// <summary>
    /// Per area (same order): lines lower than 10 px were left out (docs/spec/ocr.md "Accuracy", small text). Shorter than
    /// the areas or empty: no such lines.
    /// </summary>
    public IReadOnlyList<bool> TooSmallPerArea { get; init; } = [];

    /// <summary>More than <see cref="OcrLimits.MaxTextBoxes"/> texts were found over all areas; only that many were read.</summary>
    public bool TooManyTexts { get; init; }

    /// <summary>Areas whose pixels had not changed: their last reading was given again without the models (watching only).</summary>
    public int ReusedAreas { get; init; }
}
