using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Settings;

/// <summary>Creates the <see cref="IPluginSettings"/> handed to a plugin.</summary>
public interface IPluginSettingsFactory
{
    IPluginSettings Create(string pluginId);
}

public sealed class PluginSettingsFactory(IDbContextFactory<OadmDbContext> dbFactory) : IPluginSettingsFactory
{
    public IPluginSettings Create(string pluginId) => new PluginSettings(dbFactory, pluginId);
}

/// <summary>
/// Settings of one plugin, stored in the Settings table under <c>Plugin:&lt;pluginId&gt;:&lt;key&gt;</c>,
/// so a plugin can only see its own keys. Setting a null value deletes the key.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    private readonly IDbContextFactory<OadmDbContext> _dbFactory;
    private readonly string _prefix;

    public PluginSettings(IDbContextFactory<OadmDbContext> dbFactory, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (pluginId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin id must not contain ':'.", nameof(pluginId));
        }

        _dbFactory = dbFactory;
        PluginId = pluginId;
        _prefix = SettingKeys.PluginPrefix + pluginId + ":";
    }

    public string PluginId { get; }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var fullKey = FullKey(key);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == fullKey, ct).ConfigureAwait(false);
        return row?.ValueJson;
    }

    public async Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        var fullKey = FullKey(key);
        if (valueJson is not null)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(valueJson);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException($"Value of '{key}' is not valid JSON.", nameof(valueJson), ex);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await SettingRows.WriteAsync(db, fullKey, valueJson, ct).ConfigureAwait(false);
    }

    private string FullKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _prefix + key;
    }

    public override string ToString() => $"PluginSettings({PluginId})";
}
