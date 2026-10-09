using System.Globalization;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>One selectable time zone: IANA id, readable label with the UTC offset (like ADM), and the offset for sorting.</summary>
/// <param name="Id">IANA id the device receives, e.g. "Europe/Vienna".</param>
/// <param name="Label">"(UTC+01:00) Vienna - Europe/Vienna".</param>
/// <param name="BaseOffset">Standard (non daylight saving) offset from UTC.</param>
/// <param name="ObservesDaylightSaving">The zone switches to daylight saving time this year.</param>
/// <param name="HasOffset">
/// False when the OS time zone database of this computer does not know the zone (e.g. Antarctica/Troll on Windows
/// Server 2025): no offset in the label, sorted last, no daylight saving option. Devices with the Time API still get the
/// IANA id and apply their own rules; converting it to a POSIX string for older firmware is refused.
/// </param>
public sealed record TimeZoneEntry(string Id, string Label, TimeSpan BaseOffset, bool ObservesDaylightSaving, bool HasOffset = true)
{
    /// <summary>"UTC+01:00", "Unknown" when the OS does not know the zone.</summary>
    public string OffsetText => HasOffset ? TimeZoneCatalog.FormatOffset(BaseOffset).Trim('(', ')') : "Unknown";

    /// <summary>"Vienna" (type-to-search in the time zone list).</summary>
    public string City => string.Equals(Id, "UTC", StringComparison.Ordinal) ? "Coordinated Universal Time" : TimeZoneCatalog.City(Id);

    /// <summary>"(UTC+01:00) Vienna" like the ADM drop-down; "Troll" without a known offset. City names are unique.</summary>
    public string ShortLabel => HasOffset ? $"{TimeZoneCatalog.FormatOffset(BaseOffset)} {City}" : City;

    public override string ToString() => Label;
}

/// <summary>
/// The IANA time zones AXIS OS offers (the 313 entries of <c>getTimeZoneList</c> on AXIS OS 12.11, bundled), with
/// readable labels "(UTC+01:00) Vienna - Europe/Vienna" sorted by offset like the ADM / Windows time zone list.
/// Offsets come from the server or client OS time zone database (<see cref="TimeZoneInfo"/>, IANA ids on all
/// three OS; standard offset and daylight saving of the current year, <see cref="ZoneYear"/>); ids the OS does not
/// know keep the list entry without an offset ("Troll - Antarctica/Troll"), sorted last.
/// </summary>
public static class TimeZoneCatalog
{
    private static readonly Lazy<IReadOnlyList<TimeZoneEntry>> LazyAll = new(Load);
    private static readonly Lazy<HashSet<string>> LazyIds = new(() => new HashSet<string>(All.Select(z => z.Id), StringComparer.Ordinal));

    /// <summary>All zones, sorted by base offset, then label; zones without a known offset last.</summary>
    public static IReadOnlyList<TimeZoneEntry> All => LazyAll.Value;

    /// <summary>True for an IANA id of the bundled list (case-sensitive, as the device expects it).</summary>
    public static bool IsKnown(string? id) => id is not null && LazyIds.Value.Contains(id);

    public static TimeZoneEntry? Find(string? id) => id is null ? null : All.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Zones newer than some OS time zone databases (e.g. the ICU of Windows 10 1809), mapped to an older id with the
    /// same current rules. Antarctica/Troll has no equivalent and stays without an OS zone there.
    /// Antarctica/Vostok is UTC+05 without daylight saving since December 2023, like Asia/Tashkent.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["Europe/Kyiv"] = "Europe/Kiev",
        ["Pacific/Kanton"] = "Pacific/Enderbury",
        ["America/Ciudad_Juarez"] = "America/Denver",
        ["America/Coyhaique"] = "America/Punta_Arenas",
        ["Asia/Urumqi"] = "Asia/Dhaka",
        ["Antarctica/Vostok"] = "Asia/Tashkent",
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

    /// <summary>The list entry for <paramref name="id"/>; <paramref name="zone"/> null = the OS does not know it.</summary>
    internal static TimeZoneEntry Describe(string id, TimeZoneInfo? zone, int year)
    {
        if (string.Equals(id, "UTC", StringComparison.Ordinal))
        {
            return new TimeZoneEntry(id, "(UTC) Coordinated Universal Time - UTC", TimeSpan.Zero, false);
        }

        if (zone is null)
        {
            return new TimeZoneEntry(id, $"{City(id)} - {id}", TimeSpan.Zero, false, HasOffset: false);
        }

        var rules = ZoneYear.Of(zone, year);
        return new TimeZoneEntry(id, $"{FormatOffset(rules.Standard)} {City(id)} - {id}", rules.Standard, rules.ObservesDaylightSaving);
    }

    /// <summary>Sorted like <see cref="All"/>: known offsets first by offset, then label.</summary>
    internal static List<TimeZoneEntry> Sort(IEnumerable<TimeZoneEntry> zones) =>
        [.. zones.OrderBy(z => !z.HasOffset).ThenBy(z => z.BaseOffset).ThenBy(z => z.Label, StringComparer.OrdinalIgnoreCase)];

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

        var year = System.DateTime.UtcNow.Year;
        return Sort(ids.Select(id => Describe(id, TryGetSystemZone(id), year)));
    }
}
