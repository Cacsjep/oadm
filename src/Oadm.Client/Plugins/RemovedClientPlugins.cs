using Oadm.Client.Infrastructure;
using Oadm.Contracts.Updates;

namespace Oadm.Client.Plugins;

/// <summary>
/// The client's part of docs/spec/plugin-removal.md: the plugin settings (<c>&lt;id&gt;/&lt;key&gt;</c>) of ids the previous start
/// had from the bundled plugins and this one no longer knows are deleted once; then the new set is kept. Nothing without the
/// shipped manifest (a development run), when a plugin failed to load or when a newer client stored the set (an older client
/// sharing the data folder).
/// </summary>
public static class RemovedClientPlugins
{
    /// <summary>Returns the ids whose settings were deleted. <paramref name="version"/> null: the stored version is ignored.</summary>
    public static IReadOnlyList<string> Clean(IClientSettingsStore store, IReadOnlySet<string>? bundled, IReadOnlySet<string> known, bool loadFailures, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(known);
        if (bundled is null || loadFailures)
        {
            return [];
        }

        var settings = store.Current;
        if (version is not null && settings.BundledPluginsVersion is { } stored && UpdateVersions.IsOlder(version, stored))
        {
            return [];
        }

        var gone = (settings.BundledPlugins ?? []).Where(id => !known.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        foreach (var key in settings.PluginSettings.Keys.ToList())
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && gone.Contains(key[..slash]))
            {
                settings.PluginSettings.Remove(key);
                if (!deleted.Contains(key[..slash], StringComparer.OrdinalIgnoreCase))
                {
                    deleted.Add(key[..slash]);
                }
            }
        }

        var now = bundled.Order(StringComparer.Ordinal).ToList();
        var versionChanged = version is not null && !string.Equals(settings.BundledPluginsVersion, version, StringComparison.Ordinal);
        if (deleted.Count > 0 || versionChanged || settings.BundledPlugins is null || !now.SequenceEqual(settings.BundledPlugins, StringComparer.OrdinalIgnoreCase))
        {
            settings.BundledPlugins = now;
            settings.BundledPluginsVersion = version ?? settings.BundledPluginsVersion;
            store.Save();
        }

        return deleted;
    }
}
