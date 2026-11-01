namespace Oadm.Contracts.Vulnerabilities;

/// <summary>
/// A firmware version as numbers compared part by part ("6.50.5.19", "11.11.93", "12.10.36"); missing parts count as 0, so
/// 12.10 equals 12.10.0. Text after the numbers ("11.11.124_beta") is ignored.
/// </summary>
public readonly struct FirmwareVersion : IComparable<FirmwareVersion>, IEquatable<FirmwareVersion>
{
    private const int MaxParts = 6;
    private readonly int[]? _parts;

    private FirmwareVersion(int[] parts) => _parts = parts;

    /// <summary>The numbers ("12.10.36": 12, 10, 36).</summary>
    public IReadOnlyList<int> Parts => _parts ?? [];

    public static bool TryParse(string? text, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var parts = new List<int>(4);
        int i = 0;
        while (i < span.Length && parts.Count < MaxParts)
        {
            int start = i;
            while (i < span.Length && char.IsAsciiDigit(span[i]))
            {
                i++;
            }

            if (i == start || i - start > 9)
            {
                break;
            }

            parts.Add(int.Parse(span[start..i], System.Globalization.CultureInfo.InvariantCulture));
            if (i < span.Length && span[i] == '.' && i + 1 < span.Length && char.IsAsciiDigit(span[i + 1]))
            {
                i++;
                continue;
            }

            break;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        version = new FirmwareVersion([.. parts]);
        return true;
    }

    /// <summary>Whether this version is <paramref name="track"/> or one of its builds (6.50.5.19 is in track 6.50).</summary>
    public bool IsInTrack(FirmwareVersion track)
    {
        var mine = Parts;
        var prefix = track.Parts;
        if (prefix.Count == 0 || mine.Count < prefix.Count)
        {
            return prefix.Count > 0 && CompareTo(track) == 0;
        }

        for (int i = 0; i < prefix.Count; i++)
        {
            if (mine[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    public int CompareTo(FirmwareVersion other)
    {
        var a = Parts;
        var b = other.Parts;
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            int x = i < a.Count ? a[i] : 0;
            int y = i < b.Count ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    public bool Equals(FirmwareVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is FirmwareVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        var parts = Parts;
        int last = parts.Count - 1;
        while (last >= 0 && parts[last] == 0)
        {
            last--; // 12.10 and 12.10.0 are equal, so they hash alike
        }

        for (int i = 0; i <= last; i++)
        {
            hash.Add(parts[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join('.', Parts);

    public static bool operator ==(FirmwareVersion left, FirmwareVersion right) => left.Equals(right);

    public static bool operator !=(FirmwareVersion left, FirmwareVersion right) => !left.Equals(right);

    public static bool operator <(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) >= 0;
}
