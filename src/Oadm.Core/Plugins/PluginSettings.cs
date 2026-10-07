using System.Collections.Concurrent;

using Oadm.Core.Settings;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// Hands each plugin its own namespaced <see cref="IPluginSettings"/>. Production implementation:
/// <see cref="DbPluginSettingsProvider"/> (Settings table); <see cref="InMemoryPluginSettingsProvider"/> for tests.
/// </summary>
public interface IPluginSettingsProvider
{
    /// <exception cref="ArgumentException">Empty plugin id or one containing ':'.</exception>
    IPluginSettings GetSettings(string pluginId);
}

/// <summary>Volatile settings for tests. Same key layout and validation as the database store.</summary>
public sealed class InMemoryPluginSettingsProvider : IPluginSettingsProvider
{
    private readonly ConcurrentDictionary<string, string?> _values = new(StringComparer.Ordinal);

    public IPluginSettings GetSettings(string pluginId)
    {
        SettingKeys.ValidatePluginId(pluginId);
        return new Scoped(this, pluginId);
    }

    private sealed class Scoped(InMemoryPluginSettingsProvider owner, string pluginId) : IPluginSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct)
        {
            return Task.FromResult(owner._values.TryGetValue(SettingKeys.PluginKey(pluginId, key), out var value) ? value : null);
        }

        public Task SetAsync(string key, string? valueJson, CancellationToken ct)
        {
            var fullKey = SettingKeys.PluginKey(pluginId, key);
            SettingKeys.ValidateJson(key, valueJson);
            if (valueJson is null)
            {
                owner._values.TryRemove(fullKey, out _);
            }
            else
            {
                owner._values[fullKey] = valueJson;
            }

            return Task.CompletedTask;
        }
    }
}
