namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>The OADM server's time zone as an AXIS IANA id ("Synchronize with server computer time" sets it on the devices).</summary>
public static class ServerTimeZone
{
    /// <summary>IANA id of <paramref name="zone"/> (Windows ids are converted) when AXIS devices know it, else null.</summary>
    public static string? Resolve(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var candidates = new List<string>();
        if (zone.HasIanaId)
        {
            candidates.Add(zone.Id);
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) && TimeZoneInfo.TryConvertWindowsIdToIanaId(windows, out var canonical))
            {
                candidates.Add(canonical);
            }
        }
        else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana))
        {
            candidates.Add(iana);
        }

        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime)
        {
            candidates.Add("UTC");
        }

        return candidates.FirstOrDefault(TimeZoneCatalog.IsKnown);
    }
}
