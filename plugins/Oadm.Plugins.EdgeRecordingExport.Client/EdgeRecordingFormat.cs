using System.Globalization;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>The page's texts for sizes, counts and times (this computer's time).</summary>
public static class EdgeRecordingFormat
{
    /// <summary>The format of the start and stop fields: "2026-10-12 08:00".</summary>
    public const string InputFormat = "yyyy-MM-dd HH:mm";

    /// <summary>"1 recording", "340 recordings".</summary>
    public static string Count(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    /// <summary>"about 48 GB", "Unknown" without an estimate.</summary>
    public static string AboutSize(long? bytes) => bytes is { } b ? "about " + Size(b) : "Unknown";

    /// <summary>"512 KB", "3.2 MB", "48 GB".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} bytes")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>A time in this computer's time zone, "2026-10-12 20:09:25".</summary>
    public static string Local(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Reads "2026-10-12 08:00" (seconds optional) as this computer's time; null when it is not a time.</summary>
    public static DateTimeOffset? ParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd"];
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local).ToUniversalTime()
            : null;
    }
}
