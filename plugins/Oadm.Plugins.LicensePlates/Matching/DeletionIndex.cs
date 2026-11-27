namespace Oadm.Plugins.LicensePlates.Matching;

/// <summary>
/// The near-match index of one set of plates (symmetric deletion): every plate is filed under each text it gives with up to
/// as many characters deleted as it may deviate. A read finds its candidates by deleting up to the deviation of its own
/// characters and looking each result up: two texts within N wrong, missing or extra characters always meet at a text with at
/// most N deletions on both sides. Keys are 64-bit hashes of those texts in one sorted array (a collision only adds a
/// candidate, every candidate is checked with <see cref="EditDistance"/>), so a lookup is a few binary searches, never a
/// scan of the plates.
/// </summary>
internal sealed class DeletionIndex
{
    private readonly ulong[] _keys;
    private readonly int[] _plates;

    private DeletionIndex(string[] texts, ulong[] keys, int[] plates, int deviation)
    {
        Texts = texts;
        _keys = keys;
        _plates = plates;
        Deviation = deviation;
    }

    /// <summary>The plates of the set, by their index in the entries.</summary>
    public IReadOnlyList<string> Texts { get; }

    /// <summary>The deviation the index was built for.</summary>
    public int Deviation { get; }

    /// <summary>How many entries the index holds (tests, the scale numbers).</summary>
    public int Entries => _keys.Length;

    /// <summary>
    /// The most a plate of <paramref name="length"/> characters may deviate: the setting, except plates shorter than 6
    /// characters, which deviate at most 1 with the setting 2.
    /// </summary>
    public static int MaxFor(int length, int deviation) => deviation >= 2 && length < 6 ? 1 : deviation;

    /// <summary>The index of <paramref name="texts"/> for <paramref name="deviation"/> (1 or 2).</summary>
    public static DeletionIndex Build(IReadOnlyCollection<string> texts, int deviation)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var plates = texts.ToArray();
        var keys = new List<ulong>(plates.Length * (deviation >= 2 ? 30 : 9));
        var owners = new List<int>(keys.Capacity);
        for (var i = 0; i < plates.Length; i++)
        {
            var index = i;
            Variants(plates[i], MaxFor(plates[i].Length, deviation), key =>
            {
                keys.Add(key);
                owners.Add(index);
            });
        }

        var keyArray = keys.ToArray();
        var ownerArray = owners.ToArray();
        Array.Sort(keyArray, ownerArray);
        return new DeletionIndex(plates, keyArray, ownerArray, deviation);
    }

    /// <summary>The plates that may be within the deviation of <paramref name="read"/> (to be checked with <see cref="EditDistance"/>).</summary>
    public IReadOnlyCollection<string> Candidates(string read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var found = new HashSet<int>();
        Variants(read, Deviation, key =>
        {
            var at = LowerBound(key);
            for (var i = at; i < _keys.Length && _keys[i] == key; i++)
            {
                found.Add(_plates[i]);
            }
        });
        return [.. found.Select(i => Texts[i])];
    }

    private int LowerBound(ulong key)
    {
        int lo = 0, hi = _keys.Length;
        while (lo < hi)
        {
            var mid = (int)((uint)(lo + hi) >> 1);
            if (_keys[mid] < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The hashes of <paramref name="text"/> with 0 to <paramref name="deletions"/> characters deleted (at most 2).</summary>
    internal static void Variants(string text, int deletions, Action<ulong> add)
    {
        add(Hash(text, -1, -1));
        if (deletions < 1 || text.Length < 1)
        {
            return;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && text[i] == text[i - 1])
            {
                continue; // deleting either of two equal neighbours gives the same text
            }

            add(Hash(text, i, -1));
            if (deletions < 2)
            {
                continue;
            }

            for (var j = i + 1; j < text.Length; j++)
            {
                add(Hash(text, i, j));
            }
        }
    }

    /// <summary>FNV-1a over the characters of <paramref name="text"/> without the positions <paramref name="skip1"/> and <paramref name="skip2"/>, with the length.</summary>
    private static ulong Hash(string text, int skip1, int skip2)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i == skip1 || i == skip2)
            {
                continue;
            }

            var c = text[i];
            hash = (hash ^ (byte)c) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
            length++;
        }

        return (hash ^ (ulong)length) * prime;
    }
}
