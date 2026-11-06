using System.Globalization;

using Oadm.Core.Plugins;

namespace Oadm.Server.Hosting;

/// <summary>
/// The installed plugins (<c>&lt;datafolder&gt;/plugins</c>) of a service whose data folder had to be fixed at this start
/// (docs/spec/security.md, Folders): until now others could write the data folder, so a plugin folder in it may come from
/// anyone and would run as SYSTEM / root. The whole folder is moved aside, out of the plugin roots
/// (<c>&lt;datafolder&gt;/plugins-not-loaded-&lt;time&gt;</c>), where an administrator can check it and move it back. A
/// folder that cannot be moved is not loaded, and a marker file keeps it unloaded at the next starts until the move works.
/// A normal start changes nothing.
/// </summary>
internal static partial class InstalledPluginsTrust
{
    /// <summary>Start of the name of the folder the plugins are moved to, followed by the UTC time.</summary>
    public const string AsidePrefix = "plugins-not-loaded-";

    /// <summary>The file in the data folder that says: the plugins could not be moved aside, do not load them.</summary>
    public const string MarkerFileName = "plugins-not-trusted";

    /// <summary>
    /// True when <c>&lt;datafolder&gt;/plugins</c> may be among the plugin roots of this start: always on a normal start,
    /// and after the folder was moved aside (it is gone then). False when it holds plugins that could not be moved.
    /// </summary>
    /// <param name="move">Moves a folder (tests: a move that fails); <see cref="Directory.Move"/> by default.</param>
    public static bool Check(string dataDirectory, bool dataFolderFixed, TimeProvider time, ILogger logger, Action<string, string>? move = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(time);
        var root = PluginPaths.Installed(dataDirectory);
        var marker = Path.Combine(dataDirectory, MarkerFileName);
        if (!dataFolderFixed && !File.Exists(marker))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                var name = AsidePrefix + time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var aside = Path.Combine(dataDirectory, name);
                for (var n = 2; Directory.Exists(aside) || File.Exists(aside); n++)
                {
                    aside = Path.Combine(dataDirectory, string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
                }

                (move ?? Directory.Move)(root, aside);
                LogSetAside(logger, root, aside);
            }

            File.Delete(marker);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotSetAside(logger, ex, root);
            try
            {
                File.WriteAllText(marker, "The plugins in the folder plugins are not loaded: the data folder could be changed by users other than administrators." + Environment.NewLine);
            }
            catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException)
            {
                LogMarkerFailed(logger, markerError, marker);
            }

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The data folder could be changed by users other than administrators until this start. The plugins in {Folder} are not loaded: they were moved to {Aside}. Check them and move them back to load them again")]
    private static partial void LogSetAside(ILogger logger, string folder, string aside);

    [LoggerMessage(Level = LogLevel.Error, Message = "The data folder could be changed by users other than administrators until this start. The plugins in {Folder} are not loaded; the folder could not be moved aside and stays unloaded until it can")]
    private static partial void LogNotSetAside(ILogger logger, Exception ex, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "The marker {Marker} could not be written: the plugins may load at the next start")]
    private static partial void LogMarkerFailed(ILogger logger, Exception ex, string marker);
}
