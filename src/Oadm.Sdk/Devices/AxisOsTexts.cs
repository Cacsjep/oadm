namespace Oadm.Sdk.Devices;

/// <summary>
/// The name of the device firmware as Axis calls it, one phrase everywhere: the AXIS OS column, the Upgrade AXIS OS task,
/// "AXIS OS 12.11.77". Searches that match names of columns, tasks or checks also find them by "firmware" (the ADM word).
/// </summary>
public static class AxisOsTexts
{
    /// <summary>"AXIS OS": the column header, the compliance check, every text about the device firmware.</summary>
    public const string Name = "AXIS OS";

    /// <summary>The task and its permission that install a newer AXIS OS version.</summary>
    public const string UpgradeTask = "Upgrade AXIS OS";

    /// <summary>The permission (and the task title of a downgrade) that installs an older AXIS OS version.</summary>
    public const string DowngradeTask = "Downgrade AXIS OS";

    /// <summary>The word users coming from ADM type for AXIS OS; searches over names treat it as <see cref="Name"/>.</summary>
    public const string SearchAlias = "firmware";

    /// <summary>The shortest start of "firmware" that already reads as AXIS OS ("firm").</summary>
    private const int MinAliasStart = 4;

    /// <summary>"AXIS OS 12.11.77".</summary>
    public static string Version(string version) => Name + " " + version;

    /// <summary>
    /// True when <paramref name="text"/> contains <paramref name="term"/> (ignoring case), or contains it with "firmware"
    /// read as "AXIS OS": "firmware" finds "Upgrade AXIS OS", "upgrade firmware" too, and so does a start of the word
    /// while it is typed ("firm"). O(length of the texts).
    /// </summary>
    public static bool Contains(string? text, string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Contains(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (term.Length >= MinAliasStart && SearchAlias.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return text.Contains(Name, StringComparison.OrdinalIgnoreCase);
        }

        return term.Contains(SearchAlias, StringComparison.OrdinalIgnoreCase)
            && text.Contains(term.Replace(SearchAlias, Name, StringComparison.OrdinalIgnoreCase), StringComparison.CurrentCultureIgnoreCase);
    }
}
