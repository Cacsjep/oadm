namespace Oadm.Plugins.LicensePlates.Matching;

/// <summary>
/// The character deviation between two plates: a wrong, a missing and an extra character each count as 1 (the Levenshtein
/// distance). Plates are short (at most 12 characters), so the table is small; the work stops once the distance is over the
/// bound.
/// </summary>
public static class EditDistance
{
    /// <summary>The distance of <paramref name="a"/> and <paramref name="b"/>, or <paramref name="max"/> + 1 when it is larger.</summary>
    public static int Bounded(string a, string b, int max)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > max || b.Length > 64)
        {
            return max + 1;
        }

        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                current[j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > max)
            {
                return max + 1;
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return Math.Min(previous[b.Length], max + 1);
    }
}
