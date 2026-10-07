namespace Oadm.Plugins.NtpServer.Protocol;

/// <summary>
/// NTP 64-bit timestamps (RFC 5905 section 6): 32 bits seconds since 1900-01-01 UTC plus 32 bits fraction. The seconds
/// wrap every 2^32 s (era 1 starts 2036-02-07 06:28:16 UTC); <see cref="ToDateTime"/> picks the era closest to a pivot
/// (normally the local clock), so the codec keeps working across 2036.
/// </summary>
public static class NtpTimestamp
{
    public static readonly DateTime Epoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Start of NTP era 1 (seconds counter wraps to 0).</summary>
    public static readonly DateTime Era1 = Epoch.AddSeconds(4294967296d);

    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const long EraSeconds = 1L << 32;

    /// <summary>Encodes a UTC time (any era) as an NTP timestamp.</summary>
    public static ulong FromDateTime(DateTime utc)
    {
        var ticks = utc.ToUniversalTime().Ticks - Epoch.Ticks;
        var seconds = Math.DivRem(ticks, TicksPerSecond, out var remainder);
        if (remainder < 0)
        {
            seconds--;
            remainder += TicksPerSecond;
        }

        var era = (ulong)(seconds & (EraSeconds - 1));
        var fraction = (ulong)(((UInt128)(ulong)remainder << 32) / TicksPerSecond);
        return (era << 32) | fraction;
    }

    public static ulong FromDateTimeOffset(DateTimeOffset utc) => FromDateTime(utc.UtcDateTime);

    /// <summary>Decodes an NTP timestamp to the UTC time of the era closest to <paramref name="pivotUtc"/>.</summary>
    public static DateTime ToDateTime(ulong timestamp, DateTime pivotUtc)
    {
        var seconds = (long)(timestamp >> 32);
        var fraction = timestamp & 0xFFFFFFFF;
        var pivotSeconds = (pivotUtc.ToUniversalTime().Ticks - Epoch.Ticks) / TicksPerSecond;
        var low = pivotSeconds - (EraSeconds / 2);
        var offset = (seconds - low) % EraSeconds;
        if (offset < 0)
        {
            offset += EraSeconds;
        }

        var absolute = low + offset;
        var fractionTicks = (long)((fraction * (ulong)TicksPerSecond) >> 32);
        return new DateTime(Epoch.Ticks + (absolute * TicksPerSecond) + fractionTicks, DateTimeKind.Utc);
    }

    /// <summary>NTP short format (16.16 seconds) used for root delay and root dispersion; clamped to 0..65535.99 s.</summary>
    public static uint ToShort(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return 0;
        }

        var value = seconds * 65536d;
        return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    public static double FromShort(uint value) => value / 65536d;

    /// <summary>Difference <paramref name="a"/> - <paramref name="b"/> in seconds (both decoded around the same pivot).</summary>
    public static double Difference(ulong a, ulong b, DateTime pivotUtc) =>
        (ToDateTime(a, pivotUtc) - ToDateTime(b, pivotUtc)).TotalSeconds;
}
