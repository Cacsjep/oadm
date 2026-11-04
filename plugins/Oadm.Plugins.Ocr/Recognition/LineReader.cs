using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// Makes a line from the reader's output (docs/spec/ocr.md "Reading" step 4 and "Accuracy"): the free reading (CTC best path);
/// with a format, the format-guided match replaces the frames it covers, so the line's text holds the match as the format
/// wants it and the rest as read. Lines whose mean character probability is below 50 % are dropped. The line's confidence is
/// the lowest character probability of the match, or of the whole line without one.
/// </summary>
internal static class LineReader
{
    /// <summary>A match is taken only when its weakest character is at least this likely.</summary>
    internal const float MinMatchProbability = 0.005f;

    /// <summary>
    /// A match is taken only when the format cost at most this much against the free reading (log probability): about two
    /// characters the reader had as unlikely; a format forced onto other text costs far more.
    /// </summary>
    internal const double MinMatchLogScore = -10;

    /// <summary>The line, or null when nothing was read or it is below the drop rule.</summary>
    public static RecognizedLine? Read(LineScores scores, CharacterList characters, FormatPattern? pattern, RelativeBox box)
    {
        var free = CtcDecoder.Greedy(scores, characters);
        free = Trim(free, characters);
        if (free.Count == 0)
        {
            return null;
        }

        var mean = free.Average(c => c.Probability);
        GuidedMatch? match = null;
        if (pattern is not null)
        {
            match = FormatGuidedDecoder.Decode(scores, characters, pattern);
            if (match is not null && !Accept(match, free, characters))
            {
                match = null;
            }
        }

        if (match is null && mean < OnnxTextRecognizer.DropBelow)
        {
            return null;
        }

        var chars = match is null ? free : Compose(free, match, characters);
        var text = CtcDecoder.Text(chars);
        var confidence = match is not null
            ? match.MinProbability
            : chars.Where(c => c.Class != characters.SpaceClass).Select(c => c.Probability).DefaultIfEmpty(0).Min();
        return new RecognizedLine(
            new TextLine(text, box, Math.Round(confidence * 100, 1)),
            chars.Select(c => new CharProbability(c.Text, c.Probability)).ToList(),
            match?.Formatted);
    }

    /// <summary>Whether the guided match is a real one and not forced onto other text.</summary>
    private static bool Accept(GuidedMatch match, List<DecodedChar> free, CharacterList characters)
    {
        if (!match.AllDashesRead || !DashesAsRead(match, free) || !match.CheckDigitValid || match.MinProbability < MinMatchProbability || match.LogScore < MinMatchLogScore)
        {
            return false;
        }

        // The free reading of the same frames must have about as many characters (spaces and dashes aside).
        var freeCount = free.Count(c => c.FirstFrame >= match.FirstFrame && c.LastFrame <= match.LastFrame && !IsSeparator(c, characters));
        var matchCount = match.Chars.Count(c => !IsSeparator(c, characters));
        return Math.Abs(freeCount - matchCount) <= Math.Max(1, matchCount / 4);
    }

    /// <summary>
    /// A match with dashes holds the dashes the reader read there, no other: the format never makes one up ("12X34" for ##-##) and
    /// never leaves one out ("2026-11-04" for ####-####). A format without a dash behaves as it did.
    /// </summary>
    private static bool DashesAsRead(GuidedMatch match, List<DecodedChar> free)
    {
        var dashes = 0;
        foreach (var dash in match.Chars)
        {
            if (dash.Text != "-")
            {
                continue;
            }

            dashes++;
            if (!free.Exists(c => c.Text == "-" && c.FirstFrame <= dash.LastFrame && c.LastFrame >= dash.FirstFrame))
            {
                return false;
            }
        }

        return dashes == 0 || dashes == free.Count(c => c.Text == "-" && c.LastFrame >= match.FirstFrame && c.FirstFrame <= match.LastFrame);
    }

    /// <summary>
    /// The free reading before and after the match, a space on either side of it, and the match in the format's layout (its
    /// spaces where the format has them, gaps the reader saw inside it dropped).
    /// </summary>
    private static List<DecodedChar> Compose(List<DecodedChar> free, GuidedMatch match, CharacterList characters)
    {
        var result = new List<DecodedChar>();
        result.AddRange(free.Where(c => c.LastFrame < match.FirstFrame));
        if (result.Count > 0)
        {
            result.Add(Space(characters, match.FirstFrame));
        }

        for (var i = 0; i < match.Chars.Count; i++)
        {
            if (i > 0 && i < match.SpaceBefore.Count && match.SpaceBefore[i])
            {
                result.Add(Space(characters, match.Chars[i].FirstFrame));
            }

            result.Add(match.Chars[i]);
        }

        var after = free.Where(c => c.FirstFrame > match.LastFrame).ToList();
        if (after.Count > 0)
        {
            result.Add(Space(characters, match.LastFrame));
            result.AddRange(after);
        }

        // No space twice, none at the ends.
        var clean = new List<DecodedChar>(result.Count);
        foreach (var c in result)
        {
            if (c.Class == characters.SpaceClass && (clean.Count == 0 || clean[^1].Class == characters.SpaceClass))
            {
                continue;
            }

            clean.Add(c);
        }

        return Trim(clean, characters);
    }

    private static DecodedChar Space(CharacterList characters, int frame) => new(" ", characters.SpaceClass, 1, frame, frame);

    private static List<DecodedChar> Trim(List<DecodedChar> chars, CharacterList characters)
    {
        var start = 0;
        var end = chars.Count;
        while (start < end && chars[start].Class == characters.SpaceClass)
        {
            start++;
        }

        while (end > start && chars[end - 1].Class == characters.SpaceClass)
        {
            end--;
        }

        return start == 0 && end == chars.Count ? chars : chars.GetRange(start, end - start);
    }

    private static bool IsSeparator(DecodedChar c, CharacterList characters) => c.Class == characters.SpaceClass || c.Text is "-";
}
