using System.Globalization;

namespace Oadm.Sdk.Formatting;

/// <summary>
/// The one text of a number of bytes, for the server and the client: "512 bytes", "412 KB", "87.0 MB", "1.2 GB".
/// Steps of 1,024; whole numbers below 1 MB, one decimal from MB on; the decimal point on every computer (a task step or
/// a report written on the server reads the same in every client). A value that rounds up to 1,024 moves to the next
/// unit ("1.0 MB", never "1024 KB"). SDK 0.12.0.
/// </summary>
public static class ByteSize
{
    private const double Step = 1024;

    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>"0 bytes", "1 byte", "1023 bytes", "1 KB", "412 KB", "87.0 MB", "1.2 GB", "3.5 TB"; a negative number keeps its sign ("-1.5 MB").</summary>
    public static string Text(long bytes)
    {
        var unit = UnitOf(bytes);
        return Number(bytes, unit) + " " + UnitName(bytes, unit);
    }

    /// <summary>
    /// How far a transfer is, both numbers in the unit of <paramref name="total"/>: "37.0 of 82.4 MB", "12 of 412 KB".
    /// Without a total (zero or less) the text of <paramref name="done"/> alone.
    /// </summary>
    public static string Progress(long done, long total)
    {
        if (total <= 0)
        {
            return Text(done);
        }

        var unit = UnitOf(total);
        return Number(done, unit) + " of " + Number(total, unit) + " " + UnitName(total, unit);
    }

    /// <summary>-1 for bytes, else the index in <see cref="Units"/> whose rounded number stays below 1,024 (the last unit takes the rest).</summary>
    private static int UnitOf(long bytes)
    {
        var value = Math.Abs((double)bytes);
        var unit = -1;
        while (unit < Units.Length - 1 && Rounded(value, unit) >= Step)
        {
            value /= Step;
            unit++;
        }

        return unit;
    }

    private static string Number(long bytes, int unit)
    {
        if (unit < 0)
        {
            return bytes.ToString(CultureInfo.InvariantCulture);
        }

        var value = Rounded(bytes / Math.Pow(Step, unit + 1), unit);
        return value.ToString(Decimals(unit) == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    private static string UnitName(long bytes, int unit) => unit >= 0 ? Units[unit] : bytes is 1 or -1 ? "byte" : "bytes";

    private static double Rounded(double value, int unit) => Math.Round(value, Decimals(unit), MidpointRounding.AwayFromZero);

    /// <summary>Bytes and KB are whole numbers; MB and larger have one decimal.</summary>
    private static int Decimals(int unit) => unit <= 0 ? 0 : 1;
}
