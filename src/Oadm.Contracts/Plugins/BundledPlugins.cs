using System.Text.Json;

namespace Oadm.Contracts.Plugins;

/// <summary>
/// The shipped manifest of the bundled plugins (docs/spec/plugin-removal.md): <c>plugins/bundled.json</c> next to the plugin
/// folders of a published server and client, <c>{ "plugins": ["oadm.acap", ...] }</c>, written by <c>publish_plugins</c> of the
/// manage scripts. A root with a manifest is a bundled root: a folder whose plugin.json id it does not list is skipped and,
/// where the process may write, deleted. A root without one (development, <c>--plugin-dir</c>, the data folder) loads as before.
/// </summary>
public static class BundledPlugins
{
    public const string FileName = "bundled.json";

    /// <summary>A folder renamed for its delete: deleted at the next start when the delete failed, never loaded.</summary>
    public const string RemovingSuffix = ".removing";

    /// <summary>One folder of a bundled root that is not part of this version.</summary>
    /// <param name="Id">Its plugin.json id.</param>
    /// <param name="Folder">The folder that stays and must not load; null when it was deleted.</param>
    public sealed record SkippedFolder(string Id, string? Folder);

    /// <summary>The ids of the manifest in <paramref name="root"/>; null when the root has none or it cannot be read.</summary>
    public static IReadOnlySet<string>? ReadManifest(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var path = Path.Combine(root, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return plugins.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                .Select(p => p.GetString()!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The id of plugin.json in <paramref name="folder"/>, or null.</summary>
    public static string? PackageId(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var path = Path.Combine(folder, "plugin.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()?.Trim()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> lies inside a macOS app bundle (<c>*.app/Contents</c>): never deleted there, it breaks the signature.</summary>
    public static bool IsInsideAppBundle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir?.Parent is not null; dir = dir.Parent)
        {
            if (dir.Name == "Contents" && dir.Parent.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Before loading a root: every folder with a plugin.json whose id the manifest does not list is skipped; with
    /// <paramref name="allowDelete"/> it is renamed (fails cleanly when read-only: then only skipped) and the renamed folder
    /// deleted; a renamed folder a crash left is deleted now. Empty for a root without a manifest. Never throws.
    /// </summary>
    public static IReadOnlyList<SkippedFolder> Clean(string root, bool allowDelete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (ReadManifest(root) is not { } shipped)
        {
            return [];
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var skipped = new List<SkippedFolder>();
        foreach (var folder in folders.Order(StringComparer.Ordinal))
        {
            if (folder.EndsWith(RemovingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!allowDelete || !TryDelete(folder))
                {
                    skipped.Add(new SkippedFolder(PackageId(folder) ?? Path.GetFileName(folder), folder));
                }

                continue;
            }

            if (PackageId(folder) is not { } id || shipped.Contains(id))
            {
                continue;
            }

            if (!allowDelete)
            {
                skipped.Add(new SkippedFolder(id, folder));
                continue;
            }

            var renamed = folder + RemovingSuffix;
            try
            {
                if (Directory.Exists(renamed) && !TryDelete(renamed))
                {
                    skipped.Add(new SkippedFolder(id, folder));
                    skipped.Add(new SkippedFolder(id, renamed));
                    continue;
                }

                Directory.Move(folder, renamed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedFolder(id, folder)); // read-only: skip loading only
                continue;
            }

            skipped.Add(new SkippedFolder(id, TryDelete(renamed) ? null : renamed));
        }

        return skipped;
    }

    /// <summary>
    /// <c>--self-check</c>: what is wrong with the manifest of a published root: missing, or not listing exactly the plugin
    /// folders next to it. Empty when it is right.
    /// </summary>
    public static IReadOnlyList<string> ManifestProblems(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (ReadManifest(root) is not { } listed)
        {
            return [$"{FileName} is missing in {root}."];
        }

        var folders = (Directory.Exists(root) ? Directory.GetDirectories(root) : [])
            .Select(PackageId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. folders.Where(id => !listed.Contains(id)).Order(StringComparer.Ordinal).Select(id => $"{FileName} does not list the plugin folder {id}."),
            .. listed.Where(id => !folders.Contains(id)).Order(StringComparer.Ordinal).Select(id => $"{FileName} lists {id}, which has no plugin folder."),
        ];
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(folder);
        }
    }
}
