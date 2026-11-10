using System.Globalization;
using System.Text;
using System.Text.Json;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Models;

/// <summary>
/// The class names of a model (docs/spec/vision-center.md "Models"): the model's metadata <c>names</c> (Ultralytics writes a
/// Python dict, <c>{0: 'person', 1: 'bicycle'}</c>; a JSON list or object works too), else a labels file (one name per
/// line), else "Class 0", "Class 1", ... A name "N/A" stays as the gap it names.
/// </summary>
internal static class ClassNames
{
    /// <summary>The metadata key Ultralytics exports write.</summary>
    public const string MetadataKey = "names";

    /// <summary>The names of a labels file: one per line, blank lines at the end left out, null for too many.</summary>
    public static IReadOnlyList<string>? FromLabels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > VisionLimits.MaxClasses)
        {
            return null;
        }

        return [.. lines.Select((name, i) => Clean(name, i))];
    }

    /// <summary>The names of the metadata value, or null when it holds none that can be read.</summary>
    public static IReadOnlyList<string>? FromMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var byId = text.StartsWith('[') ? FromJsonList(text) : FromDict(text);
        if (byId is null || byId.Count == 0)
        {
            return null;
        }

        var count = byId.Keys.Max() + 1;
        if (count > VisionLimits.MaxClasses)
        {
            return null;
        }

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = byId.TryGetValue(i, out var name) ? Clean(name, i) : ModelTexts.ClassName(i);
        }

        return names;
    }

    /// <summary>Whether every name is only its own id ("0", "1", ...): an export that kept no names.</summary>
    public static bool OnlyIds(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] != i.ToString(CultureInfo.InvariantCulture))
            {
                return false;
            }
        }

        return names.Count > 0;
    }

    /// <summary>"Class 0", "Class 1", ... for <paramref name="count"/> classes.</summary>
    public static IReadOnlyList<string> Defaults(int count) => [.. Enumerable.Range(0, Math.Max(0, count)).Select(ModelTexts.ClassName)];

    /// <summary>A name shown and chosen: trimmed, at most 64 characters, "Class 7" for an empty one.</summary>
    public static string Clean(string? name, int id)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ModelTexts.ClassName(id);
        }

        return trimmed.Length <= VisionLimits.MaxNameLength ? trimmed : trimmed[..VisionLimits.MaxNameLength];
    }

    private static Dictionary<int, string>? FromJsonList(string text)
    {
        try
        {
            var list = JsonSerializer.Deserialize<string[]>(text);
            return list is null ? null : list.Select((n, i) => (n, i)).ToDictionary(p => p.i, p => p.n ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A Python dict or JSON object of class id to name: <c>{0: 'person', 1: "it's"}</c>.</summary>
    private static Dictionary<int, string>? FromDict(string text)
    {
        if (!text.StartsWith('{') || !text.EndsWith('}'))
        {
            return null;
        }

        var result = new Dictionary<int, string>();
        var i = 1;
        while (true)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length - 1)
            {
                return result;
            }

            // The key: a number, or a number in quotes (JSON).
            var quotedKey = text[i] is '"' or '\'';
            var key = quotedKey ? ReadQuoted(text, ref i) : ReadNumber(text, ref i);
            if (key is null || !int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id >= VisionLimits.MaxClasses)
            {
                return null;
            }

            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] != ':')
            {
                return null;
            }

            i++;
            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] is not ('"' or '\'') || ReadQuoted(text, ref i) is not { } name)
            {
                return null;
            }

            result[id] = name;
            SkipSpace(text, ref i);
            if (i < text.Length && text[i] == ',')
            {
                i++;
            }
            else if (i < text.Length - 1)
            {
                return null;
            }
        }
    }

    private static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }

    private static string? ReadNumber(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        return i > start ? text[start..i] : null;
    }

    /// <summary>A string in single or double quotes with backslash escapes (<c>\'</c>, <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\uXXXX</c>).</summary>
    private static string? ReadQuoted(string text, ref int i)
    {
        var quote = text[i++];
        var value = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == quote)
            {
                return value.ToString();
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (i >= text.Length)
            {
                return null;
            }

            var escaped = text[i++];
            switch (escaped)
            {
                case 'n':
                    value.Append(' ');
                    break;
                case 't':
                    value.Append(' ');
                    break;
                case 'u' when i + 4 <= text.Length && int.TryParse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    value.Append((char)code);
                    i += 4;
                    break;
                default:
                    value.Append(escaped);
                    break;
            }
        }

        return null;
    }
}
