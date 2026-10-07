namespace Oadm.Core.Plugins;

/// <summary>Well-known plugin folders.</summary>
public static class PluginPaths
{
    /// <summary><c>&lt;dataFolder&gt;/plugins</c>: installed plugins.</summary>
    public static string Installed(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        return Path.Combine(dataFolder, "plugins");
    }

    /// <summary>
    /// <c>&lt;app folder&gt;/plugins</c>: plugins shipped next to a published server, including
    /// single-file builds. Uses AppContext.BaseDirectory, never Assembly.Location.
    /// </summary>
    public static string Bundled(string? appDirectory = null) =>
        Path.Combine(appDirectory ?? AppContext.BaseDirectory, "plugins");

    /// <summary>
    /// <c>&lt;repo&gt;/artifacts/plugins</c>: where plugin projects in this repository copy their
    /// build output (see plugins/*/README). Found by walking up from <paramref name="startDirectory"/>
    /// (default: the app base directory) to the folder containing Oadm.sln. Null outside a checkout.
    /// </summary>
    public static string? Development(string? startDirectory = null)
    {
        var repo = FindRepositoryRoot(startDirectory ?? AppContext.BaseDirectory);
        return repo is null ? null : Path.Combine(repo, "artifacts", "plugins");
    }

    /// <summary>The nearest ancestor folder containing Oadm.sln, or null.</summary>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Oadm.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
