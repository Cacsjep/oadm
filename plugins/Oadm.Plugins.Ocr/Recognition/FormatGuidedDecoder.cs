namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>What one position of a format allows (docs/spec/ocr.md "Expected text").</summary>
internal enum FormatTokenKind
{
    Digit,
    Letter,
    LetterOrDigit,
    Literal,
}

/// <summary>One position of a format.</summary>
/// <param name="Kind">Digit, letter, either or a literal character.</param>
/// <param name="Literal">The character of a literal.</param>
/// <param name="Optional">A dash, which may be missing in the text.</param>
/// <param name="SpaceBefore">The format has a space before it: the text may have one there (or none).</param>
internal readonly record struct FormatToken(FormatTokenKind Kind, char Literal, bool Optional, bool SpaceBefore = false);

/// <summary>
/// A format of an area's expected text, parsed: <c>#</c> a digit, <c>A</c> a letter, <c>*</c> a letter or digit, every other
/// character as written; a space or a dash of the format may be missing in the text. Gaps the reader saw between the
/// characters (the drums of a meter) do not break a match: the match is written in the format's own layout. A format of 4 + 7 positions starting <c>AAAU</c> is a container number with an ISO 6346 check digit.
/// </summary>
internal sealed class FormatPattern
{
    private FormatPattern(string format, IReadOnlyList<FormatToken> tokens, bool iso6346)
    {
        Format = format;
        Tokens = tokens;
        IsContainerNumber = iso6346;
    }

    /// <summary>The format as written.</summary>
    public string Format { get; }

    /// <summary>The positions (spaces dropped).</summary>
    public IReadOnlyList<FormatToken> Tokens { get; }

    /// <summary>Container number: the owner code, U, six digits and the ISO 6346 check digit.</summary>
    public bool IsContainerNumber { get; }

    /// <summary>The format's positions, or null for a format without any.</summary>
    public static FormatPattern? Parse(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var tokens = new List<FormatToken>();
        var space = false;
        foreach (var ch in format)
        {
            if (ch == ' ')
            {
                space = true;
                continue;
            }

            switch (ch)
            {
                case '#':
                    tokens.Add(new FormatToken(FormatTokenKind.Digit, ch, false));
                    break;
                case 'A':
                    tokens.Add(new FormatToken(FormatTokenKind.Letter, ch, false));
                    break;
                case '*':
                    tokens.Add(new FormatToken(FormatTokenKind.LetterOrDigit, ch, false));
                    break;
                case '-':
                    tokens.Add(new FormatToken(FormatTokenKind.Literal, ch, true));
                    break;
                default:
                    tokens.Add(new FormatToken(FormatTokenKind.Literal, ch, false));
                    break;
            }

            if (space)
            {
                tokens[^1] = tokens[^1] with { SpaceBefore = true };
                space = false;
            }
        }

        if (tokens.Count == 0 || tokens.All(t => t.Optional))
        {
            return null;
        }

        var compact = string.Concat(format.Where(c => c is not (' ' or '-')));
        var iso = compact.Length == 11 && compact.StartsWith("AAAU", StringComparison.Ordinal) && compact[4..].All(c => c == '#');
        return new FormatPattern(format, tokens, iso);
    }
}

/// <summary>A format match found in one line's recognition output.</summary>
/// <param name="Chars">The characters of the match, with their probabilities and frames.</param>
/// <param name="FirstFrame">The match's first frame.</param>
/// <param name="LastFrame">The match's last frame.</param>
/// <param name="LogScore">The match's log probability minus the free reading's over the same frames (0 at best, below when the format forced a choice).</param>
/// <param name="CheckDigitValid">For a container number: the ISO 6346 check digit holds; true for every other format.</param>
internal sealed record GuidedMatch(IReadOnlyList<DecodedChar> Chars, int FirstFrame, int LastFrame, double LogScore, bool CheckDigitValid)
{
    /// <summary>Per character: the format has a space before it (the match is written with it).</summary>
    public IReadOnlyList<bool> SpaceBefore { get; init; } = [];

    /// <summary>The match in the format's layout: its characters with the format's spaces.</summary>
    public string Formatted => string.Concat(Chars.Select((c, i) => (i > 0 && i < SpaceBefore.Count && SpaceBefore[i] ? " " : string.Empty) + c.Text));

    /// <summary>The match's text.</summary>
    public string Text => CtcDecoder.Text(Chars);

    /// <summary>0 to 1: the lowest probability of its characters.</summary>
    public float MinProbability => Chars.Count == 0 ? 0 : Chars.Min(c => c.Probability);
}

/// <summary>
/// Format-guided CTC decoding (docs/spec/ocr.md "Accuracy"): the best alignment of the format to a part of a line's frames,
/// with the character choice limited per position while decoding (digits where <c>#</c>, letters where <c>A</c>), not fixed
/// afterwards. A Viterbi search over the states before the match (free reading), each position with each allowed character,
/// the gaps between positions (blank or space) and after the match (free reading). A container number whose check digit does
/// not hold gets the best one-character change that makes it hold.
/// </summary>
internal static class FormatGuidedDecoder
{
    private const float Floor = 1e-8f;
    private const int CandidatesPerFrame = 8;

    /// <summary>
    /// The cost (log probability) of going from one character straight to the next without a blank frame between. Legal in
    /// CTC and common in dense print, but without a cost the search splits one character's frames into two characters to
    /// dodge a forced choice ("1" then "I" over the frames of one "I").
    /// </summary>
    private const double DirectStep = 1.5;

    /// <summary>The best match of <paramref name="pattern"/> in <paramref name="scores"/>, or null when the line is too short.</summary>
    public static GuidedMatch? Decode(LineScores scores, CharacterList characters, FormatPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(pattern);
        var tokens = pattern.Tokens;
        var n = tokens.Count;
        var frames = scores.Frames;
        if (frames < tokens.Count(t => !t.Optional))
        {
            return null;
        }

        var seen = CandidateClasses(scores);
        var classes = new int[n][];
        for (var j = 0; j < n; j++)
        {
            classes[j] = AllowedClasses(tokens[j], j, pattern, characters, seen);
            if (classes[j].Length == 0)
            {
                if (!tokens[j].Optional)
                {
                    return null;
                }
            }
        }

        // State layout: 0 = before (P), 1 = after (S), then per token its gap state and its character states.
        var gapState = new int[n];
        var charStart = new int[n];
        var stateCount = 2;
        for (var j = 0; j < n; j++)
        {
            gapState[j] = stateCount++;
            charStart[j] = stateCount;
            stateCount += classes[j].Length;
        }

        // next[j + 1]: the tokens reachable after token j (j = -1: the first ones); a dash may be skipped.
        var next = new int[n + 1][];
        for (var j = -1; j < n; j++)
        {
            var list = new List<int>();
            var k = j + 1;
            while (k < n)
            {
                if (classes[k].Length > 0)
                {
                    list.Add(k);
                }

                if (!tokens[k].Optional)
                {
                    break;
                }

                k++;
            }

            next[j + 1] = [.. list];
        }

        // preds[j]: the tokens after which token j may come (-1: the state before the match).
        var preds = new List<int>[n];
        for (var j = 0; j < n; j++)
        {
            preds[j] = [];
        }

        for (var i = -1; i < n; i++)
        {
            foreach (var j in next[i + 1])
            {
                preds[j].Add(i);
            }
        }

        var canEnd = new bool[n];
        for (var j = 0; j < n; j++)
        {
            canEnd[j] = AllOptionalAfter(tokens, j);
        }

        var prev = new double[stateCount];
        var cur = new double[stateCount];
        var back = new int[frames * stateCount];
        var free = new double[frames];
        for (var t = 0; t < frames; t++)
        {
            var row = scores.Frame(t);
            var max = 0f;
            foreach (var p in row)
            {
                max = Math.Max(max, p);
            }

            free[t] = Math.Log(Math.Max(max, Floor));
        }

        Array.Fill(prev, double.NegativeInfinity);
        {
            var row = scores.Frame(0);
            prev[0] = free[0];
            foreach (var j in next[0])
            {
                for (var k = 0; k < classes[j].Length; k++)
                {
                    prev[charStart[j] + k] = Log(row[classes[j][k]]);
                    back[charStart[j] + k] = 0;
                }
            }
        }

        // Per token: the best and second best of its character states in the previous frame (for "a different character").
        var best1 = new double[n];
        var best1State = new int[n];
        var best2 = new double[n];
        var best2State = new int[n];
        for (var t = 1; t < frames; t++)
        {
            var row = scores.Frame(t);
            var gapLog = Log(row[CharacterList.BlankClass] + row[characters.SpaceClass]);
            for (var j = 0; j < n; j++)
            {
                best1[j] = best2[j] = double.NegativeInfinity;
                best1State[j] = best2State[j] = -1;
                for (var k = 0; k < classes[j].Length; k++)
                {
                    var s = charStart[j] + k;
                    var v = prev[s];
                    if (v > best1[j])
                    {
                        best2[j] = best1[j];
                        best2State[j] = best1State[j];
                        best1[j] = v;
                        best1State[j] = s;
                    }
                    else if (v > best2[j])
                    {
                        best2[j] = v;
                        best2State[j] = s;
                    }
                }
            }

            var backRow = t * stateCount;

            // Before the match: free reading.
            cur[0] = prev[0] + free[t];
            back[backRow] = 0;

            // After the match: free reading, entered from the last character.
            {
                var bestValue = prev[1];
                var bestFrom = 1;
                for (var j = 0; j < n; j++)
                {
                    if (canEnd[j] && best1[j] > bestValue)
                    {
                        bestValue = best1[j];
                        bestFrom = best1State[j];
                    }
                }

                cur[1] = bestValue + free[t];
                back[backRow + 1] = bestFrom;
            }

            for (var j = 0; j < n; j++)
            {
                // Gap after token j: stay, or come from a character of j.
                var g = gapState[j];
                if (prev[g] >= best1[j])
                {
                    cur[g] = prev[g] + gapLog;
                    back[backRow + g] = g;
                }
                else
                {
                    cur[g] = best1[j] + gapLog;
                    back[backRow + g] = best1State[j];
                }
            }

            for (var j = 0; j < n; j++)
            {
                for (var k = 0; k < classes[j].Length; k++)
                {
                    var s = charStart[j] + k;
                    var bestValue = prev[s];
                    var bestFrom = s;

                    // Enter token j from the state before the match, from a gap or directly from a different character.
                    foreach (var i in preds[j])
                    {
                        if (i < 0)
                        {
                            if (prev[0] > bestValue)
                            {
                                bestValue = prev[0];
                                bestFrom = 0;
                            }

                            continue;
                        }

                        if (prev[gapState[i]] > bestValue)
                        {
                            bestValue = prev[gapState[i]];
                            bestFrom = gapState[i];
                        }

                        // Directly from a character of token i: only a different class (the same twice needs a blank between).
                        var sameClass = classes[i].Length > 0 && best1State[i] >= 0 && classes[i][best1State[i] - charStart[i]] == classes[j][k];
                        var v = (sameClass ? best2[i] : best1[i]) - DirectStep;
                        var from = sameClass ? best2State[i] : best1State[i];
                        if (from >= 0 && v > bestValue)
                        {
                            bestValue = v;
                            bestFrom = from;
                        }
                    }

                    cur[s] = bestValue + Log(row[classes[j][k]]);
                    back[backRow + s] = bestFrom;
                }
            }

            (prev, cur) = (cur, prev);
        }

        // The end: after the match, or the match ends in the last frame.
        var endValue = prev[1];
        var endState = 1;
        for (var j = 0; j < n; j++)
        {
            if (!canEnd[j])
            {
                continue;
            }

            for (var k = 0; k < classes[j].Length; k++)
            {
                if (prev[charStart[j] + k] > endValue)
                {
                    endValue = prev[charStart[j] + k];
                    endState = charStart[j] + k;
                }
            }
        }

        if (double.IsNegativeInfinity(endValue))
        {
            return null;
        }

        // Back through the frames.
        var path = new int[frames];
        var state = endState;
        for (var t = frames - 1; t >= 0; t--)
        {
            path[t] = state;
            state = back[t * stateCount + state];
        }

        var stateToken = new int[stateCount];
        var stateClass = new int[stateCount];
        Array.Fill(stateToken, -1);
        for (var j = 0; j < n; j++)
        {
            for (var k = 0; k < classes[j].Length; k++)
            {
                stateToken[charStart[j] + k] = j;
                stateClass[charStart[j] + k] = classes[j][k];
            }
        }

        var chars = new List<DecodedChar>();
        var segments = new List<(int Token, int First, int Last)>();
        int first = -1, last = -1;
        for (var t = 0; t < frames; t++)
        {
            var s = path[t];
            if (s is 0 or 1)
            {
                continue;
            }

            if (first < 0)
            {
                first = t;
            }

            last = t;
            var token = stateToken[s];
            if (token < 0)
            {
                continue;
            }

            if (t > 0 && path[t - 1] == s)
            {
                var c = chars[^1];
                chars[^1] = c with { Probability = Math.Max(c.Probability, scores.Frame(t)[stateClass[s]]), LastFrame = t };
                segments[^1] = (token, segments[^1].First, t);
            }
            else
            {
                chars.Add(new DecodedChar(characters[stateClass[s]], stateClass[s], scores.Frame(t)[stateClass[s]], t, t));
                segments.Add((token, t, t));
            }
        }

        if (chars.Count == 0)
        {
            return null;
        }

        var logScore = 0.0;
        for (var t = first; t <= last; t++)
        {
            var s = path[t];
            var row = scores.Frame(t);
            var e = stateToken[s] >= 0 ? Log(row[stateClass[s]]) : Log(row[CharacterList.BlankClass] + row[characters.SpaceClass]);
            logScore += e - free[t];
        }

        var valid = true;
        if (pattern.IsContainerNumber)
        {
            valid = Iso6346.IsValid(CtcDecoder.Text(chars));
            if (!valid)
            {
                var fixedChars = FixCheckDigit(scores, characters, chars, segments, classes);
                if (fixedChars is not null)
                {
                    logScore += fixedChars.Value.Cost;
                    chars = fixedChars.Value.Chars;
                    valid = true;
                }
            }
        }

        return new GuidedMatch(chars, first, last, logScore, valid) { SpaceBefore = [.. segments.Select(seg => tokens[seg.Token].SpaceBefore)] };
    }

    /// <summary>The best change of one character that makes the ISO 6346 check digit hold, with its cost in log probability.</summary>
    private static (List<DecodedChar> Chars, double Cost)? FixCheckDigit(
        LineScores scores,
        CharacterList characters,
        List<DecodedChar> chars,
        List<(int Token, int First, int Last)> segments,
        int[][] classes)
    {
        (List<DecodedChar> Chars, double Cost)? best = null;
        for (var i = 0; i < chars.Count; i++)
        {
            var (token, first, last) = segments[i];
            var current = SegmentLog(scores, chars[i].Class, first, last);
            foreach (var c in classes[token])
            {
                if (c == chars[i].Class)
                {
                    continue;
                }

                var cost = SegmentLog(scores, c, first, last) - current;
                if (best is not null && cost <= best.Value.Cost)
                {
                    continue;
                }

                var candidate = new List<DecodedChar>(chars);
                var p = 0f;
                for (var t = first; t <= last; t++)
                {
                    p = Math.Max(p, scores.Frame(t)[c]);
                }

                candidate[i] = chars[i] with { Class = c, Text = characters[c], Probability = p };
                if (Iso6346.IsValid(CtcDecoder.Text(candidate)))
                {
                    best = (candidate, cost);
                }
            }
        }

        return best;
    }


    private static double SegmentLog(LineScores scores, int c, int first, int last)
    {
        var sum = 0.0;
        for (var t = first; t <= last; t++)
        {
            sum += Log(scores.Frame(t)[c]);
        }

        return sum;
    }

    private static bool AllOptionalAfter(IReadOnlyList<FormatToken> tokens, int j)
    {
        for (var k = j + 1; k < tokens.Count; k++)
        {
            if (!tokens[k].Optional)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The classes among the most likely ones of any frame: the only ones a match can use (keeps the search small).</summary>
    private static HashSet<int> CandidateClasses(LineScores scores)
    {
        var set = new HashSet<int>();
        Span<int> top = stackalloc int[CandidatesPerFrame];
        Span<float> topP = stackalloc float[CandidatesPerFrame];
        for (var t = 0; t < scores.Frames; t++)
        {
            var row = scores.Frame(t);
            topP.Fill(-1);
            for (var c = 1; c < row.Length; c++)
            {
                var p = row[c];
                if (p <= topP[^1])
                {
                    continue;
                }

                var k = CandidatesPerFrame - 1;
                while (k > 0 && topP[k - 1] < p)
                {
                    topP[k] = topP[k - 1];
                    top[k] = top[k - 1];
                    k--;
                }

                topP[k] = p;
                top[k] = c;
            }

            for (var k = 0; k < CandidatesPerFrame; k++)
            {
                if (topP[k] > 0)
                {
                    set.Add(top[k]);
                }
            }
        }

        return set;
    }

    private static int[] AllowedClasses(FormatToken token, int index, FormatPattern pattern, CharacterList characters, HashSet<int> seen)
    {
        IEnumerable<int> all = token.Kind switch
        {
            FormatTokenKind.Digit => characters.Digits,
            FormatTokenKind.Letter when pattern.IsContainerNumber && index < 3 => characters.UpperLatinLetters,
            FormatTokenKind.Letter => characters.Letters,
            FormatTokenKind.LetterOrDigit => characters.Digits.Concat(characters.Letters),
            _ => characters.ClassOf(token.Literal.ToString()) is var c and >= 0 ? [c] : [],
        };

        // A literal is always a candidate; for the classes only those the model considered somewhere, plus the digits (cheap,
        // and the check digit may need one the model never ranked high).
        return token.Kind switch
        {
            FormatTokenKind.Literal => all.ToArray(),
            FormatTokenKind.Digit => all.ToArray(),
            _ => all.Where(c => seen.Contains(c) || characters[c] is [>= '0' and <= '9']).Distinct().ToArray(),
        };
    }

    private static double Log(float p) => Math.Log(Math.Max(p, Floor));

}
