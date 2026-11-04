using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// Everything <see cref="TextMatcher"/> makes of an area's lines: the fields of the event Text read (docs/spec/ocr.md
/// "Events and the rule action") and what the page shows. A richer form of <see cref="AreaMatch"/>.
/// </summary>
public sealed record TextMatch
{
    /// <summary>No text: an empty area.</summary>
    public static TextMatch Empty { get; } = new();

    /// <summary>
    /// The event field Text: with a format the chosen match (written in the format's form, look-alikes fixed; empty without
    /// a match), else all lines joined for the events (<see cref="TextRead"/>).
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The event field Text read: all lines joined with a space (a line ending "-" without one), at most 500 characters.</summary>
    public string TextRead { get; init; } = string.Empty;

    /// <summary>All lines in reading order, joined with line breaks (the page shows one line per row).</summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>Every different match of the format in reading order, at most <see cref="TextMatcher.MaxMatches"/>; empty without a format.</summary>
    public IReadOnlyList<string> AllMatches { get; init; } = [];

    /// <summary>The event field All matches: <see cref="AllMatches"/> separated by a space.</summary>
    public string AllMatchesText => string.Join(' ', AllMatches);

    /// <summary>The event field Number: <see cref="Text"/> as a number (<see cref="NumberText.Of"/>); empty when it is none.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>
    /// 0 to 100: the lowest confidence of the lines that make <see cref="Text"/>; 0 without text. A line's confidence is the
    /// lowest probability of its characters (of its format match when it has one; the recognition sets it), so this is the
    /// spec's lowest character probability of the value.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>The event field Matches format: the area's format matched; always false for Any text (no format to match).</summary>
    public bool MatchesFormat { get; init; }

    /// <summary>The area's lines in reading order: top to bottom, left to right.</summary>
    public IReadOnlyList<TextLine> Lines { get; init; } = [];

    /// <summary>Where <see cref="Text"/> was read (the union of its lines' boxes, for the page's cut-out); null without text.</summary>
    public RelativeBox? TextBox { get; init; }

    /// <summary>How the value's line lies in the picture; null when not known (a value joined over two lines): the cut-out stays level.</summary>
    public LineTurn? Turn { get; init; }

    /// <summary>The frame this reading comes from (a reference, in the vote only); empty for an empty value.</summary>
    public ReadOnlyMemory<byte> Frame { get; init; }

    /// <summary>Value, matches format, raw text.</summary>
    public AreaMatch ToAreaMatch() => new(Text, MatchesFormat, RawText);

    /// <summary>The model's form for <see cref="ITextMatcher"/>: with every match, the confidence and the value's box.</summary>
    public AreaValue ToAreaValue() => new(ToAreaMatch(), AllMatches, Text.Length == 0 ? 0 : Confidence, Text.Length == 0 ? null : TextBox) { MatchTurn = Text.Length == 0 ? null : Turn };
}
