using Microsoft.Extensions.Logging;

using Oadm.Contracts.Plugins;
using Oadm.Core.Plugins;

namespace Oadm.Server.Plugins;

/// <summary>
/// The server's part of docs/spec/plugin-removal.md: before loading, bundled folders the shipped manifest does not list are
/// skipped and deleted (<see cref="PrepareRoots"/>); after loading, the state of ids the previous start had from the bundled
/// root and this one no longer knows is deleted once (<see cref="KnownIds"/>, <see cref="BundledIds"/>, <see cref="RemovedPlugins"/>).
/// </summary>
public static partial class BundledPluginsAtStart
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The roots with a shipped manifest (never <paramref name="installedRoot"/>, the data folder's plugins): their folders of
    /// ids the manifest does not list are skipped (one log line each) and deleted where the server may write, never inside a
    /// macOS app bundle. Sets <see cref="PluginLoader.SkipFolder"/>. Returns the full paths of those roots.
    /// </summary>
    public static IReadOnlyList<string> PrepareRoots(PluginLoader loader, IEnumerable<string> roots, string installedRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(logger);
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedRoot));
        var bundledRoots = new List<string>();
        var skipped = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))))
        {
            if (string.Equals(root, installed, PathComparison) || !Directory.Exists(root) || BundledPlugins.ReadManifest(root) is null)
            {
                continue;
            }

            bundledRoots.Add(root);
            foreach (var folder in BundledPlugins.Clean(root, allowDelete: !BundledPlugins.IsInsideAppBundle(root)))
            {
                LogSkipped(logger, folder.Id);
                if (folder.Folder is { } left)
                {
                    skipped.Add(Path.GetFullPath(left));
                }
            }
        }

        loader.SkipFolder = skipped.Count == 0 ? null : skipped.Contains;
        return bundledRoots;
    }

    /// <summary>
    /// The package ids loaded from <paramref name="bundledRoots"/> plus every task, core and action plugin id they registered;
    /// null without a bundled root (a development run: nothing is cleaned or stored).
    /// </summary>
    public static IReadOnlySet<string>? BundledIds(PluginRegistry registry, IReadOnlyList<string> bundledRoots)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bundledRoots);
        if (bundledRoots.Count == 0)
        {
            return null;
        }

        bool Bundled(PluginOrigin origin) => origin.Directory is { } directory
            && bundledRoots.Any(root => Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, PathComparison));
        var packages = registry.Packages.Where(p => Bundled(p.Origin)).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        packages.UnionWith(PluginIds(registry, origin => packages.Contains(origin.PackageId)));
        return packages;
    }

    /// <summary>
    /// Every id this start knows: every package and plugin loaded from any root, the folders of the data folder's plugins,
    /// every add-on the catalogue offers or that is installed, and the merged packages.
    /// </summary>
    public static IReadOnlySet<string> KnownIds(PluginRegistry registry, string installedRoot, string addOnRoot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var known = registry.Packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        known.UnionWith(PluginIds(registry, _ => true));
        foreach (var root in new[] { installedRoot, addOnRoot })
        {
            try
            {
                foreach (var folder in Directory.Exists(root) ? Directory.GetDirectories(root) : [])
                {
                    known.Add(Path.GetFileName(folder));
                    if (BundledPlugins.PackageId(folder) is { } id)
                    {
                        known.Add(id);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: its plugins failed to load too, which deletes nothing this start.
            }
        }

        known.UnionWith(AddOns.AddOnCatalog.Entries.Select(e => e.Id));
        known.UnionWith(MergedPlugins.Ids);
        return known;
    }

    private static IEnumerable<string> PluginIds(PluginRegistry registry, Func<PluginOrigin, bool> of) =>
        registry.AllCorePlugins.Where(c => of(c.Origin)).Select(c => c.Id)
            .Concat(registry.AllTaskPlugins.Where(t => of(t.Origin)).Select(t => t.Id))
            .Concat(registry.AllActionPlugins.Where(a => of(a.Origin)).Select(a => a.Plugin.Id));

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} skipped: not part of this OADM version")]
    private static partial void LogSkipped(ILogger logger, string pluginId);
}
