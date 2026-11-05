namespace Oadm.Client.Infrastructure;

/// <summary>
/// Clients up to 4.11 unpacked their native libraries into a new folder per build (73 MB each), and nothing ever removed them. The
/// published client keeps these libraries beside the exe, so the folders are only left-overs: they go once, in the background. A folder
/// younger than a day may belong to an older client that is still running.
/// </summary>
public static class OldNativeFolders
{
    private const string AppFolder = "Oadm.Client";

    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Where the .NET host unpacks: the temp folder on Windows, the home folder elsewhere. The temp folder of Linux and macOS is shared with
    /// every other user, who could put a link there, so it is never searched.
    /// </summary>
    public static IReadOnlyList<string> Places() => Places(OperatingSystem.IsWindows());

    public static IReadOnlyList<string> Places(bool windows)
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".net", AppFolder);
        return windows ? [Path.Combine(Path.GetTempPath(), ".net", AppFolder), home] : [home];
    }

    /// <summary>Removes the old folders of <paramref name="places"/> and returns how many went. A folder in use stays.</summary>
    public static int Clean(IEnumerable<string> places, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(places);
        var removed = 0;
        foreach (var place in places.Distinct(StringComparer.Ordinal).Where(IsRealFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(place).ToList())
            {
                try
                {
                    if (utcNow - Directory.GetLastWriteTimeUtc(folder) >= MinimumAge)
                    {
                        Directory.Delete(folder, recursive: true);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or locked: the next start tries again.
                }
            }
        }

        return removed;
    }

    /// <summary>A link leads to a folder that is not ours: nothing in it goes.</summary>
    private static bool IsRealFolder(string place) => Directory.Exists(place) && new DirectoryInfo(place).LinkTarget is null;
}
