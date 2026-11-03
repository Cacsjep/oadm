namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The classes of a recognition model's output (docs/spec/ocr.md "Reading"): class 0 is the CTC blank, then one class per
/// entry of the model's character list (one entry per line of its <c>.txt</c>), then the space. Knows which classes are digits
/// and letters for the format-guided decoding.
/// </summary>
public sealed class CharacterList
{
    private readonly string[] _texts;

    /// <summary>A list from the entries of the model's character list, in order (without blank and space).</summary>
    public CharacterList(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _texts = new string[entries.Count + 2];
        _texts[0] = string.Empty;
        for (var i = 0; i < entries.Count; i++)
        {
            _texts[i + 1] = entries[i];
        }

        SpaceClass = entries.Count + 1;
        _texts[SpaceClass] = " ";

        var digits = new List<int>();
        var letters = new List<int>();
        var latinUpper = new List<int>();
        var byText = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 1; c < _texts.Length; c++)
        {
            var text = _texts[c];
            byText.TryAdd(text, c);
            if (text.Length != 1)
            {
                continue;
            }

            var ch = text[0];
            if (ch is >= '0' and <= '9')
            {
                digits.Add(c);
            }
            else if (char.IsLetter(ch) && ch <= 'ɏ')
            {
                // Latin script only: a server model's list holds every script; a format letter is a Latin letter.
                letters.Add(c);
                if (ch is >= 'A' and <= 'Z')
                {
                    latinUpper.Add(c);
                }
            }
        }

        Digits = [.. digits];
        Letters = [.. letters];
        UpperLatinLetters = [.. latinUpper];
        _byText = byText;
    }

    private readonly Dictionary<string, int> _byText;

    /// <summary>Classes of the model output: blank, the entries, space.</summary>
    public int ClassCount => _texts.Length;

    /// <summary>The CTC blank.</summary>
    public const int BlankClass = 0;

    /// <summary>The space class (the last one).</summary>
    public int SpaceClass { get; }

    /// <summary>The classes 0 to 9.</summary>
    public int[] Digits { get; }

    /// <summary>Every Latin letter class (with umlauts and accents, both cases).</summary>
    public int[] Letters { get; }

    /// <summary>The classes A to Z (container owner codes).</summary>
    public int[] UpperLatinLetters { get; }

    /// <summary>The text of class <paramref name="c"/> (empty for the blank).</summary>
    public string this[int c] => _texts[c];

    /// <summary>The class of <paramref name="text"/>, or -1 when the model has none.</summary>
    public int ClassOf(string text) => _byText.TryGetValue(text, out var c) ? c : -1;

    /// <summary>Reads a character list file: one entry per line, UTF-8.</summary>
    public static CharacterList Load(string path)
    {
        var lines = File.ReadAllText(path).Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        return new CharacterList(lines.Take(count).Select(l => l.TrimEnd('\r')).ToArray());
    }
}
