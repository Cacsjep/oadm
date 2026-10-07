using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Settings;

/// <summary>The production <see cref="IPluginSettingsProvider"/>: plugin settings in the Settings table.</summary>
public sealed class DbPluginSettingsProvider(IDbContextFactory<OadmDbContext> dbFactory) : IPluginSettingsProvider
{
    public IPluginSettings GetSettings(string pluginId) => new PluginSettings(dbFactory, pluginId);
}

/// <summary>
/// Settings of one plugin, stored in the Settings table under <c>Plugin:&lt;pluginId&gt;:&lt;key&gt;</c>
/// (see <see cref="SettingKeys.PluginKey"/>), so a plugin can only see its own keys. Setting a null
/// value deletes the key.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    private readonly IDbContextFactory<OadmDbContext> _dbFactory;

    public PluginSettings(IDbContextFactory<OadmDbContext> dbFactory, string pluginId)
    {
        SettingKeys.ValidatePluginId(pluginId);
        _dbFactory = dbFactory;
        PluginId = pluginId;
    }

    public string PluginId { get; }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var fullKey = SettingKeys.PluginKey(PluginId, key);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == fullKey, ct).ConfigureAwait(false);
        return row?.ValueJson;
    }

    public async Task SetAsync(string key, string? valueJson, CancellationToken ct)
    {
        var fullKey = SettingKeys.PluginKey(PluginId, key);
        SettingKeys.ValidateJson(key, valueJson);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await SettingRows.WriteAsync(db, fullKey, valueJson, ct).ConfigureAwait(false);
    }

    public override string ToString() => $"PluginSettings({PluginId})";
}
