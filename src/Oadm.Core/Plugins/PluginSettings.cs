using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>Hands each plugin its own namespaced <see cref="IPluginSettings"/>.</summary>
public interface IPluginSettingsProvider
{
    IPluginSettings GetSettings(string pluginId);
}

/// <summary>Volatile settings for tests and for running without a database.</summary>
public sealed class InMemoryPluginSettingsProvider : IPluginSettingsProvider
{
    private readonly ConcurrentDictionary<string, string?> _values = new(StringComparer.Ordinal);

    /// <summary>Storage key used for a plugin setting: <c>Plugin.&lt;pluginId&gt;.&lt;key&gt;</c>.</summary>
    public static string StorageKey(string pluginId, string key) => $"Plugin.{pluginId}.{key}";

    public IPluginSettings GetSettings(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new Scoped(this, pluginId);
    }

    private sealed class Scoped(InMemoryPluginSettingsProvider owner, string pluginId) : IPluginSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct)
        {
            return Task.FromResult(owner._values.TryGetValue(StorageKey(pluginId, key), out var value) ? value : null);
        }

        public Task SetAsync(string key, string? valueJson, CancellationToken ct)
        {
            if (valueJson is null)
            {
                owner._values.TryRemove(StorageKey(pluginId, key), out _);
            }
            else
            {
                owner._values[StorageKey(pluginId, key)] = valueJson;
            }

            return Task.CompletedTask;
        }
    }
}
