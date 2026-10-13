using System.Globalization;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>The page's texts for sizes, counts and times (this computer's time).</summary>
public static class EdgeRecordingFormat
{
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

    /// <summary>A day of the date picker and a time of day of the time picker as this computer's time, in UTC; null when one is missing.</summary>
    public static DateTimeOffset? Combine(DateTimeOffset? date, TimeSpan? time) =>
        date is { } day && time is { } of
            ? new DateTimeOffset(DateTime.SpecifyKind(day.Date + of, DateTimeKind.Local)).ToUniversalTime()
            : null;
}
