using Oadm.Plugins.LicensePlates.Plates;

namespace Oadm.Plugins.LicensePlates.Matching;

/// <summary>
/// The wildcard plates of the list ("AXIS12**", at most <see cref="LicensePlateLimits.MaxWildcardPlates"/>) for the matcher
/// (docs/spec/license-plate-center.md "Matching"): grouped by length and by the positions of their first three fixed
/// characters, keyed by those characters. A read looks up one key per group of its length and checks only the plates
/// under it; never a scan of every wildcard plate. Not thread safe: the plate store calls it under its lock.
/// </summary>
internal sealed class WildcardIndex
{
    private readonly Dictionary<int, Dictionary<Positions, Dictionary<string, List<string>>>> _byLength = [];
    private int _count;

    /// <summary>How many wildcard plates the index holds.</summary>
    public int Count => _count;

    /// <summary>Adds a wildcard plate (normalized, valid: at least three fixed characters).</summary>
    public void Add(string pattern)
    {
        var positions = Positions.Of(pattern);
        var key = positions.KeyOf(pattern);
        if (!_byLength.TryGetValue(pattern.Length, out var groups))
        {
            _byLength[pattern.Length] = groups = [];
        }

        if (!groups.TryGetValue(positions, out var keys))
        {
            groups[positions] = keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        if (!keys.TryGetValue(key, out var patterns))
        {
            keys[key] = patterns = [];
        }

        if (!patterns.Contains(pattern, StringComparer.Ordinal))
        {
            patterns.Add(pattern);
            _count++;
        }
    }

    public void Remove(string pattern)
    {
        var positions = Positions.Of(pattern);
        if (_byLength.TryGetValue(pattern.Length, out var groups) && groups.TryGetValue(positions, out var keys)
            && keys.TryGetValue(positions.KeyOf(pattern), out var patterns) && patterns.Remove(pattern))
        {
            _count--;
            if (patterns.Count == 0)
            {
                keys.Remove(positions.KeyOf(pattern));
            }

            if (keys.Count == 0)
            {
                groups.Remove(positions);
            }

            if (groups.Count == 0)
            {
                _byLength.Remove(pattern.Length);
            }
        }
    }

    public void Clear()
    {
        _byLength.Clear();
        _count = 0;
    }

    /// <summary>The wildcard plates a read text fits (letters and digits only).</summary>
    public List<string> Fitting(string read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var found = new List<string>();
        if (!_byLength.TryGetValue(read.Length, out var groups))
        {
            return found;
        }

        foreach (var (positions, keys) in groups)
        {
            if (keys.TryGetValue(positions.KeyOf(read), out var patterns))
            {
                foreach (var pattern in patterns)
                {
                    if (PlateText.Fits(pattern, read))
                    {
                        found.Add(pattern);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The groups a read of <paramref name="length"/> looks up (tests: bounded by the patterns' shapes, not their number).</summary>
    internal int GroupsOf(int length) => _byLength.TryGetValue(length, out var groups) ? groups.Count : 0;

    /// <summary>The positions of the first three fixed characters of a pattern.</summary>
    private readonly record struct Positions(int A, int B, int C)
    {
        public static Positions Of(string pattern)
        {
            Span<int> at = stackalloc int[3];
            var n = 0;
            for (var i = 0; i < pattern.Length && n < 3; i++)
            {
                if (pattern[i] != PlateText.Wildcard)
                {
                    at[n++] = i;
                }
            }

            return new Positions(at[0], n > 1 ? at[1] : at[0], n > 2 ? at[2] : at[0]);
        }

        public string KeyOf(string text) => string.Concat(text[A], text[B], text[C]);
    }
}
