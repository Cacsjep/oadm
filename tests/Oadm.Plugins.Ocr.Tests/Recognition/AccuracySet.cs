using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>One area of a picture of the accuracy set.</summary>
/// <param name="Name">The area's name.</param>
/// <param name="Box">Left, top, width, height, relative to the picture.</param>
/// <param name="Format">The expected text's format; null: any text.</param>
/// <param name="Expected">The value a perfect reader gives ("" for none).</param>
internal sealed record AccuracyArea(string Name, double[] Box, string? Format, string Expected)
{
    public Area ToArea() => new()
    {
        Name = Name,
        Box = new RelativeBox(Box[0], Box[1], Box[2], Box[3]),
        Expected = Format is null ? ExpectedText.AnyText : ExpectedText.WithFormat(Format),
    };
}

/// <summary>One picture of the accuracy set.</summary>
/// <param name="File">The JPEG in the set's folder.</param>
/// <param name="Group">The group the score is reported for.</param>
/// <param name="Areas">Its areas with their expected values.</param>
internal sealed record AccuracyPicture(string File, string Group, IReadOnlyList<AccuracyArea> Areas);

/// <summary>The score of one group: exact values and the character error rate.</summary>
internal sealed record GroupScore(string Group, int Values, int Exact, double CharacterErrorRate)
{
    public double ExactRate => Values == 0 ? 1 : Exact / (double)Values;
}

/// <summary>The lowest score each group may have (baseline.json, measured when the set was made).</summary>
internal sealed record GroupBaseline(double Exact, double Cer);

/// <summary>
/// The accuracy set (docs/spec/ocr.md "Accuracy"): <c>Fixtures/AccuracySet/set.json</c> lists the pictures, their areas and
/// expected values; <c>baseline.json</c> the score per group a change must keep. Values are made from the lines like the
/// matcher does (lines joined with a space, a line ending in "-" without one; with a format the best match of a line or two
/// neighbouring lines, spaces and dashes of the format optional, the ISO 6346 check digit for container numbers).
/// </summary>
internal static class AccuracySet
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The set's folder in the test output.</summary>
    public static string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AccuracySet");

    public static IReadOnlyList<AccuracyPicture> Load()
    {
        var json = File.ReadAllText(Path.Combine(Folder, "set.json"));
        return JsonSerializer.Deserialize<SetFile>(json, Json)!.Pictures;
    }

    public static IReadOnlyDictionary<string, GroupBaseline> Baseline()
    {
        var json = File.ReadAllText(Path.Combine(Folder, "baseline.json"));
        return JsonSerializer.Deserialize<BaselineFile>(json, Json)!.Groups;
    }

    /// <summary>Reads every picture and scores every group (pictures read at once, up to the recognizer's workers).</summary>
    public static async Task<(List<GroupScore> Scores, List<string> Misses)> ScoreAsync(ITextRecognizer recognizer, IReadOnlyList<AccuracyPicture> pictures, CancellationToken ct)
    {
        var reads = pictures.Select(async picture =>
        {
            var jpeg = await File.ReadAllBytesAsync(Path.Combine(Folder, picture.File), ct);
            var result = await recognizer.ReadAsync(jpeg, picture.Areas.Select(a => a.ToArea()).ToList(), ct);
            return (picture, result);
        });

        var results = await Task.WhenAll(reads);
        var perGroup = new Dictionary<string, (int Values, int Exact, int Errors, int Chars)>();
        var misses = new List<string>();
        foreach (var (picture, result) in results)
        {
            for (var i = 0; i < picture.Areas.Count; i++)
            {
                var area = picture.Areas[i];
                var value = ValueOf(result.LinesPerArea[i], area.Format);
                var expected = Normalize(area.Expected, area.Format);
                var errors = Levenshtein(value, expected);
                var g = perGroup.GetValueOrDefault(picture.Group);
                perGroup[picture.Group] = (g.Values + 1, g.Exact + (errors == 0 ? 1 : 0), g.Errors + errors, g.Chars + Math.Max(1, expected.Length));
                if (errors != 0)
                {
                    misses.Add($"{picture.File} {area.Name}: expected '{expected}', read '{value}' (lines: {string.Join(" | ", result.LinesPerArea[i].Select(l => $"{l.Text} {l.Confidence:0}"))})");
                }
            }
        }

        var scores = perGroup.OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GroupScore(g.Key, g.Value.Values, g.Value.Exact, g.Value.Errors / (double)g.Value.Chars))
            .ToList();
        return (scores, misses);
    }

    /// <summary>A score table for the test output.</summary>
    public static string Table(IEnumerable<GroupScore> scores)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{"Group",-18} {"Values",6} {"Exact",7} {"CER",7}");
        foreach (var s in scores)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{s.Group,-18} {s.Values,6} {s.ExactRate,7:P1} {s.CharacterErrorRate,7:P2}");
        }

        return sb.ToString();
    }

    /// <summary>The area's value from its lines (a small stand-in for the matcher of the Matching package).</summary>
    public static string ValueOf(IReadOnlyList<TextLine> lines, string? format)
    {
        if (format is null)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append(' ');
                }

                sb.Append(line.Text);
            }

            return Normalize(sb.ToString(), null);
        }

        var regex = FormatRegex(format);
        var container = Regex.Replace(format, "[ -]", string.Empty) is var compact && compact.Length == 11 && compact.StartsWith("AAAU", StringComparison.Ordinal);
        var candidates = new List<(string Text, double Confidence)>();
        for (var i = 0; i < lines.Count; i++)
        {
            candidates.Add((lines[i].Text, lines[i].Confidence));
            if (i + 1 < lines.Count)
            {
                var confidence = Math.Min(lines[i].Confidence, lines[i + 1].Confidence);
                candidates.Add((lines[i].Text + " " + lines[i + 1].Text, confidence));
                candidates.Add((lines[i].Text + lines[i + 1].Text, confidence));
            }
        }

        var best = string.Empty;
        var bestConfidence = -1.0;
        foreach (var (text, confidence) in candidates)
        {
            foreach (Match m in regex.Matches(text))
            {
                var value = Normalize(m.Value, format);
                if (container && !Iso6346.IsValid(value))
                {
                    continue;
                }

                if (confidence > bestConfidence || (confidence == bestConfidence && value.Length > best.Length))
                {
                    best = value;
                    bestConfidence = confidence;
                }
            }
        }

        return best;
    }

    /// <summary>For a format: without spaces and dashes (they may be missing); for any text: single spaces.</summary>
    public static string Normalize(string text, string? format) =>
        format is null ? Regex.Replace(text.Trim(), @"\s+", " ") : Regex.Replace(text, "[ -]", string.Empty);

    /// <summary>A regular expression of a format: its spaces and dashes optional, no other space allowed (docs/spec/ocr.md).</summary>
    private static Regex FormatRegex(string format)
    {
        var parts = format.Select(ch => ch switch
        {
            '#' => "[0-9]",
            'A' => @"\p{L}",
            '*' => @"[\p{L}0-9]",
            ' ' => " ?",
            '-' => "-?",
            _ => Regex.Escape(ch.ToString()),
        });
        return new Regex(string.Concat(parts), RegexOptions.CultureInvariant);
    }

    public static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private sealed record SetFile(List<AccuracyPicture> Pictures);

    private sealed record BaselineFile(Dictionary<string, GroupBaseline> Groups);
}
