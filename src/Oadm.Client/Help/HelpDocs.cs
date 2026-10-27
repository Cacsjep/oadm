namespace Oadm.Client.Help;

/// <summary>Where the offline user docs of the Help window are: the <c>docs</c> folder next to the client executable.</summary>
public static class HelpDocs
{
    /// <summary>The folder next to the client executable the installers put the docs in.</summary>
    public const string FolderName = "docs";

    /// <summary>Development: a folder with the built offline docs instead of the one next to the executable.</summary>
    public const string FolderVariable = "OADM_DOCS_DIR";

    /// <summary>The Help window without docs (a developer build).</summary>
    public const string NotInBuild = "Help is not part of this build.";

    /// <summary>The window title of Help.</summary>
    public const string WindowTitle = "Help · OADM";

    /// <summary>
    /// The full path of the docs folder, or null when there is none with a start page (<c>index.html</c>). The first with a
    /// start page of: <see cref="FolderVariable"/>, <c>docs</c> next to the executable, <c>OADM.app/Contents/Resources/docs</c>
    /// (the macOS package keeps non-code files out of <c>Contents/MacOS</c>, like the plugins).
    /// <paramref name="variable"/> and <paramref name="baseDirectory"/> replace the environment and
    /// <see cref="AppContext.BaseDirectory"/> in tests.
    /// </summary>
    public static string? FindRoot(Func<string, string?>? variable = null, string? baseDirectory = null)
    {
        var exe = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = new List<string>();
        if ((variable ?? Environment.GetEnvironmentVariable)(FolderVariable) is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured.Trim());
        }

        candidates.Add(Path.Combine(exe, FolderName));
        if (Plugins.ClientPluginLoader.AppBundleResources(exe) is { } resources)
        {
            candidates.Add(Path.Combine(resources, FolderName));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var folder = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(folder, "index.html")))
                {
                    return folder;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // not a usable path: the next place
            }
        }

        return null;
    }
}
