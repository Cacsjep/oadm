namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>The recognition model's output for one line: per time step (frame) the probability of every class.</summary>
/// <param name="Probabilities">Frames times classes, row by row.</param>
/// <param name="Frames">Time steps.</param>
/// <param name="Classes">Classes per frame (blank, characters, space).</param>
public sealed record LineScores(float[] Probabilities, int Frames, int Classes)
{
    /// <summary>The probabilities of frame <paramref name="t"/>.</summary>
    public ReadOnlySpan<float> Frame(int t) => Probabilities.AsSpan(t * Classes, Classes);
}

/// <summary>One character of a decoded line.</summary>
/// <param name="Text">The character (an entry of the character list).</param>
/// <param name="Class">Its class.</param>
/// <param name="Probability">0 to 1: the highest probability of its class in its frames.</param>
/// <param name="FirstFrame">The first frame of the character.</param>
/// <param name="LastFrame">The last frame of the character.</param>
public readonly record struct DecodedChar(string Text, int Class, float Probability, int FirstFrame, int LastFrame);

/// <summary>CTC best-path decoding (docs/spec/ocr.md "Reading" step 4): the most likely class per frame, repeats merged, blanks dropped.</summary>
public static class CtcDecoder
{
    /// <summary>The characters of <paramref name="scores"/> in frames <paramref name="from"/> to <paramref name="to"/> (inclusive).</summary>
    public static List<DecodedChar> Greedy(LineScores scores, CharacterList characters, int from = 0, int to = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(characters);
        var result = new List<DecodedChar>();
        to = Math.Min(to, scores.Frames - 1);
        var previous = CharacterList.BlankClass;
        for (var t = Math.Max(0, from); t <= to; t++)
        {
            var row = scores.Frame(t);
            var best = 0;
            var bestP = row[0];
            for (var c = 1; c < row.Length; c++)
            {
                if (row[c] > bestP)
                {
                    bestP = row[c];
                    best = c;
                }
            }

            if (best != CharacterList.BlankClass)
            {
                if (best == previous && result.Count > 0)
                {
                    var last = result[^1];
                    result[^1] = last with { Probability = Math.Max(last.Probability, bestP), LastFrame = t };
                }
                else
                {
                    result.Add(new DecodedChar(characters[best], best, bestP, t, t));
                }
            }

            previous = best;
        }

        return result;
    }

    /// <summary>The text of <paramref name="chars"/>.</summary>
    public static string Text(IEnumerable<DecodedChar> chars) => string.Concat(chars.Select(c => c.Text));
}
