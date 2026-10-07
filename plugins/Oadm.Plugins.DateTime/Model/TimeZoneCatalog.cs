using System.Globalization;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>One selectable time zone: IANA id, readable label with the UTC offset (like ADM), and the offset for sorting.</summary>
/// <param name="Id">IANA id the device receives, e.g. "Europe/Vienna".</param>
/// <param name="Label">"(UTC+01:00) Vienna - Europe/Vienna".</param>
/// <param name="BaseOffset">Standard (non daylight saving) offset from UTC.</param>
/// <param name="ObservesDaylightSaving">The zone switches to daylight saving time.</param>
public sealed record TimeZoneEntry(string Id, string Label, TimeSpan BaseOffset, bool ObservesDaylightSaving)
{
    /// <summary>"UTC+01:00" (grid column).</summary>
    public string OffsetText => TimeZoneCatalog.FormatOffset(BaseOffset).Trim('(', ')');

    /// <summary>"Vienna" (grid column).</summary>
    public string City => string.Equals(Id, "UTC", StringComparison.Ordinal) ? "Coordinated Universal Time" : TimeZoneCatalog.City(Id);

    /// <summary>"Yes" when the zone has daylight saving time (grid column).</summary>
    public string DaylightSavingText => ObservesDaylightSaving ? "Yes" : "No";

    public override string ToString() => Label;
}

/// <summary>
/// The IANA time zones AXIS OS offers (the 313 entries of <c>getTimeZoneList</c> on AXIS OS 12.11, bundled), with
/// readable labels "(UTC+01:00) Vienna - Europe/Vienna" sorted by offset like the ADM / Windows time zone list.
/// Offsets come from the server or client OS time zone database (<see cref="TimeZoneInfo"/>, IANA ids on all
/// three OS); ids the OS does not know keep the list entry without an offset ("(UTC) id").
/// </summary>
public static class TimeZoneCatalog
{
    private static readonly Lazy<IReadOnlyList<TimeZoneEntry>> LazyAll = new(Load);
    private static readonly Lazy<HashSet<string>> LazyIds = new(() => new HashSet<string>(All.Select(z => z.Id), StringComparer.Ordinal));

    /// <summary>All zones, sorted by base offset, then label.</summary>
    public static IReadOnlyList<TimeZoneEntry> All => LazyAll.Value;

    /// <summary>True for an IANA id of the bundled list (case-sensitive, as the device expects it).</summary>
    public static bool IsKnown(string? id) => id is not null && LazyIds.Value.Contains(id);

    public static TimeZoneEntry? Find(string? id) => id is null ? null : All.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.Ordinal));

    /// <summary>Case-insensitive search in id and label ("vienna", "UTC+01", "europe/"); O(n), no allocation per entry.</summary>
    public static IEnumerable<TimeZoneEntry> Search(IEnumerable<TimeZoneEntry> zones, string? text)
    {
        ArgumentNullException.ThrowIfNull(zones);
        if (string.IsNullOrWhiteSpace(text))
        {
            return zones;
        }

        var needle = text.Trim();
        var spaced = needle.Replace(' ', '_');
        return zones.Where(z => z.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || z.Id.Contains(spaced, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Zones newer than some OS time zone databases (e.g. the ICU of Windows 10 1809), mapped to an older id with the
    /// same current rules. Antarctica/Troll has no equivalent and stays without an OS zone there.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["Europe/Kyiv"] = "Europe/Kiev",
        ["Pacific/Kanton"] = "Pacific/Enderbury",
        ["America/Ciudad_Juarez"] = "America/Denver",
        ["America/Coyhaique"] = "America/Punta_Arenas",
        ["Asia/Urumqi"] = "Asia/Dhaka",
    };

    /// <summary>The OS time zone for an IANA id (or its same-rules alias), or null when the OS does not know it.</summary>
    public static TimeZoneInfo? TryGetSystemZone(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Find(id) ?? (Aliases.TryGetValue(id, out var alias) ? Find(alias) : null);

        static TimeZoneInfo? Find(string zoneId)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        }
    }

    /// <summary>"(UTC+01:00)", "(UTC-03:30)", "(UTC)".</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "(UTC)";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"(UTC{sign}{abs.Hours:00}:{abs.Minutes:00})");
    }

    /// <summary>"Vienna" for "Europe/Vienna", "Buenos Aires" for "America/Argentina/Buenos_Aires", "UTC" for "UTC".</summary>
    public static string City(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var last = id[(id.LastIndexOf('/') + 1)..];
        return last.Replace('_', ' ');
    }

    internal static TimeZoneEntry Describe(string id)
    {
        var zone = TryGetSystemZone(id);
        var offset = zone?.BaseUtcOffset ?? TimeSpan.Zero;
        var label = string.Equals(id, "UTC", StringComparison.Ordinal)
            ? "(UTC) Coordinated Universal Time - UTC"
            : $"{FormatOffset(offset)} {City(id)} - {id}";
        return new TimeZoneEntry(id, label, offset, zone?.SupportsDaylightSavingTime ?? false);
    }

    private static List<TimeZoneEntry> Load()
    {
        using var stream = typeof(TimeZoneCatalog).Assembly.GetManifestResourceStream("Oadm.Plugins.DateAndTime.TimeZones.txt")
            ?? throw new InvalidOperationException("The bundled time zone list is missing.");
        using var reader = new StreamReader(stream);
        var ids = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ids.Add(line.Trim());
            }
        }

        return [.. ids.Select(Describe).OrderBy(z => z.BaseOffset).ThenBy(z => z.Label, StringComparer.OrdinalIgnoreCase)];
    }
}
