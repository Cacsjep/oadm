using System.Globalization;

using Oadm.Core.Plugins;

namespace Oadm.Server.Hosting;

/// <summary>
/// The installed plugins (<c>&lt;datafolder&gt;/plugins</c>) of a service that found some of them loose at this start
/// (docs/spec/security.md, Folders): until now users other than administrators could change them, so they may come from
/// anyone and would run as SYSTEM / root. What was loose is moved aside, out of the plugin roots
/// (<c>&lt;datafolder&gt;/plugins-not-loaded-&lt;time&gt;</c>), where an administrator can check it and move it back:
/// the whole folder when the data folder or the plugins folder itself was loose (<see cref="LoosePlugins.All"/>), else only
/// the plugin folders that were (<see cref="LoosePlugins.Entries"/>). A loose file elsewhere in the data folder changes
/// nothing here. What cannot be moved keeps the plugins folder unloaded, and a marker file keeps it so at the next starts
/// until the move works. A normal start changes nothing.
/// </summary>
internal static partial class InstalledPluginsTrust
{
    /// <summary>Start of the name of the folder the plugins are moved to, followed by the UTC time.</summary>
    public const string AsidePrefix = "plugins-not-loaded-";

    /// <summary>
    /// The file in the data folder that says: plugins could not be moved aside, do not load the plugins folder. Its first
    /// line is for the reader; the lines after it name the entries of the plugins folder still to move (none: the whole
    /// folder).
    /// </summary>
    public const string MarkerFileName = "plugins-not-trusted";

    /// <summary>
    /// True when <c>&lt;datafolder&gt;/plugins</c> may be among the plugin roots of this start: always on a normal start,
    /// and after what was loose was moved aside. False while something loose could not be moved.
    /// </summary>
    /// <param name="loose">What the folder check of this start found (<see cref="FolderGuard.EnsureServerFoldersAsync"/>).</param>
    /// <param name="move">Moves a folder or file (tests: a move that fails); the file system by default.</param>
    public static bool Check(string dataDirectory, LoosePlugins loose, TimeProvider time, ILogger logger, Action<string, string>? move = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(loose);
        ArgumentNullException.ThrowIfNull(time);
        var root = PluginPaths.Installed(dataDirectory);
        var marker = Path.Combine(dataDirectory, MarkerFileName);
        var names = new SortedSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var all = loose.All;
        try
        {
            all |= !ReadMarker(marker, names); // what an earlier start could not move
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerUnreadable(logger, ex, marker);
            all = true;
        }

        names.UnionWith(loose.Entries.Select(e => Path.GetFileName(Path.TrimEndingDirectorySeparator(e))));
        if (!all && names.Count == 0)
        {
            return true;
        }

        move ??= Move;
        var left = new List<string>(); // entries that could not be moved
        var stuck = false; // the whole folder could not be moved
        try
        {
            if (!all)
            {
                left.AddRange(MoveEntries(dataDirectory, root, names, time, logger, move));
            }
            else if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                var aside = AsideFolder(dataDirectory, time);
                move(root, aside);
                LogSetAside(logger, root, aside);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotSetAside(logger, ex, root);
            stuck = true;
        }

        if (!stuck && left.Count == 0)
        {
            // Everything loose is aside: the folder is free for plugins again.
            try
            {
                File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogMarkerFailed(logger, ex, marker);
            }

            return true;
        }

        try
        {
            // No names: the whole folder (also for a name the marker cannot hold on one line).
            if (stuck || left.Exists(n => n.Contains('\r', StringComparison.Ordinal) || n.Contains('\n', StringComparison.Ordinal)))
            {
                left.Clear();
            }

            File.WriteAllLines(marker, [MarkerText, .. left]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerFailed(logger, ex, marker);
        }

        return false;
    }

    private const string MarkerText = "The folder plugins is not loaded: users other than administrators could change plugins in it, and they could not be moved aside.";

    /// <summary>Moves the named entries of the plugins folder into one new folder aside; returns the names it could not move.</summary>
    private static List<string> MoveEntries(
        string dataDirectory, string root, IEnumerable<string> names, TimeProvider time, ILogger logger, Action<string, string> move)
    {
        var left = new List<string>();
        string? aside = null;
        var moved = 0;
        foreach (var name in names)
        {
            var entry = Path.Combine(root, name);
            if (!Directory.Exists(entry) && !File.Exists(entry))
            {
                continue; // gone since (an administrator took it out)
            }

            try
            {
                if (aside is null)
                {
                    aside = AsideFolder(dataDirectory, time);
                    Directory.CreateDirectory(aside);
                }

                move(entry, Path.Combine(aside, name));
                moved++;
                LogPluginSetAside(logger, entry, aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(name);
                LogPluginNotSetAside(logger, ex, entry);
            }
        }

        if (aside is not null && moved == 0)
        {
            try
            {
                Directory.Delete(aside); // nothing got there
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder stays; the next start makes its own.
            }
        }

        return left;
    }

    /// <summary>
    /// Reads the names the marker lists into <paramref name="names"/>. False when the marker stands for the whole folder:
    /// it names nothing, or something that is not a plain name.
    /// </summary>
    private static bool ReadMarker(string marker, SortedSet<string> names)
    {
        if (!File.Exists(marker))
        {
            return true;
        }

        var listed = File.ReadAllLines(marker).Skip(1).Where(l => l.Length > 0).ToList();
        if (listed.Count == 0 || listed.Exists(n => n is "." or ".." || n != Path.GetFileName(n) || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return false;
        }

        names.UnionWith(listed);
        return true;
    }

    private static string AsideFolder(string dataDirectory, TimeProvider time)
    {
        var name = AsidePrefix + time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var aside = Path.Combine(dataDirectory, name);
        for (var n = 2; Directory.Exists(aside) || File.Exists(aside); n++)
        {
            aside = Path.Combine(dataDirectory, string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
        }

        return aside;
    }

    private static void Move(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else
        {
            Directory.Move(from, to);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The data folder or the plugin folder {Folder} could be changed by users other than administrators until this start. The plugins in it are not loaded: they were moved to {Aside}. Check them and move them back to load them again")]
    private static partial void LogSetAside(ILogger logger, string folder, string aside);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The plugin folder {Folder} could be changed by users other than administrators until this start. It is not loaded: it was moved to {Aside}. Check it and move it back to load it again")]
    private static partial void LogPluginSetAside(ILogger logger, string folder, string aside);

    [LoggerMessage(Level = LogLevel.Error, Message = "The plugins in {Folder} could be changed by users other than administrators until this start. They are not loaded; the folder could not be moved aside and stays unloaded until it can")]
    private static partial void LogNotSetAside(ILogger logger, Exception ex, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "The plugin folder {Folder} could be changed by users other than administrators until this start. It could not be moved aside: no installed plugin is loaded until it can")]
    private static partial void LogPluginNotSetAside(ILogger logger, Exception ex, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "The marker {Marker} could not be read: no installed plugin is trusted")]
    private static partial void LogMarkerUnreadable(ILogger logger, Exception ex, string marker);

    [LoggerMessage(Level = LogLevel.Error, Message = "The marker {Marker} could not be written or deleted: the next start may load plugins it should not, or move them aside again")]
    private static partial void LogMarkerFailed(ILogger logger, Exception ex, string marker);
}
