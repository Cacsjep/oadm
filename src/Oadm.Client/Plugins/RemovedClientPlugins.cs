using Oadm.Client.Infrastructure;

namespace Oadm.Client.Plugins;

/// <summary>
/// The client's part of docs/spec/plugin-removal.md: the plugin settings (<c>&lt;id&gt;/&lt;key&gt;</c>) of ids the previous start
/// had from the bundled plugins and this one no longer knows are deleted once; then the new set is kept. Nothing without the
/// shipped manifest (a development run) or when a plugin failed to load.
/// </summary>
public static class RemovedClientPlugins
{
    /// <summary>Returns the ids whose settings were deleted.</summary>
    public static IReadOnlyList<string> Clean(IClientSettingsStore store, IReadOnlySet<string>? bundled, IReadOnlySet<string> known, bool loadFailures)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(known);
        if (bundled is null || loadFailures)
        {
            return [];
        }

        var settings = store.Current;
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
        if (deleted.Count > 0 || settings.BundledPlugins is null || !now.SequenceEqual(settings.BundledPlugins, StringComparer.OrdinalIgnoreCase))
        {
            settings.BundledPlugins = now;
            store.Save();
        }

        return deleted;
    }
}
