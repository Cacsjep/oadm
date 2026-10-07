using System.Globalization;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// AXIS OS / firmware version such as <c>12.11.77</c>, <c>10.12.236</c> or <c>8.40.4.3</c>. Compared
/// numerically segment by segment (missing segments count as 0). The first segment is the major line
/// (8, 9, 10, 11, 12, ...). Shared between the server task and the client dialog.
/// </summary>
public sealed class AxisOsVersion : IComparable<AxisOsVersion>, IEquatable<AxisOsVersion>
{
    private readonly int[] _parts;

    private AxisOsVersion(int[] parts, string text)
    {
        _parts = parts;
        Text = text;
    }

    /// <summary>The version as written by the device or file, normalized to dots.</summary>
    public string Text { get; }

    public int Major => _parts[0];

    /// <summary>Parses "12.11.77", "12_11_77" or "12.11.77_beta" (non-numeric suffixes are ignored). Null when unparsable.</summary>
    public static AxisOsVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = new List<int>();
        foreach (var raw in text.Trim().Split(['.', '_'], StringSplitOptions.None))
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                break;
            }

            parts.Add(n);
            if (parts.Count == 5)
            {
                break;
            }
        }

        return parts.Count < 2 ? null : new AxisOsVersion([.. parts], string.Join('.', parts));
    }

    public int CompareTo(AxisOsVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var n = Math.Max(_parts.Length, other._parts.Length);
        for (var i = 0; i < n; i++)
        {
            var a = i < _parts.Length ? _parts[i] : 0;
            var b = i < other._parts.Length ? other._parts[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    public bool Equals(AxisOsVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AxisOsVersion v && Equals(v);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        var last = _parts.Length - 1;
        while (last > 0 && _parts[last] == 0)
        {
            last--;
        }

        for (var i = 0; i <= last; i++)
        {
            hash.Add(_parts[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator ==(AxisOsVersion? a, AxisOsVersion? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(AxisOsVersion? a, AxisOsVersion? b) => !(a == b);

    public static bool operator <(AxisOsVersion? a, AxisOsVersion? b) => a is null ? b is not null : a.CompareTo(b) < 0;

    public static bool operator >(AxisOsVersion? a, AxisOsVersion? b) => a is not null && a.CompareTo(b) > 0;

    public static bool operator <=(AxisOsVersion? a, AxisOsVersion? b) => !(a > b);

    public static bool operator >=(AxisOsVersion? a, AxisOsVersion? b) => !(a < b);
}
