using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>One record of a plugin (table PluginRecords, key: plugin, collection, key).</summary>
public sealed class PluginRecord
{
    public string PluginId { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = "null";
}

/// <summary>
/// The production <see cref="IPluginRecords"/>: one row per record in the PluginRecords table, scoped to one plugin. Writes
/// of <see cref="SetManyAsync"/> are one transaction; existing rows are read in batches of <see cref="LookupBatch"/> keys.
/// </summary>
public sealed class DbPluginRecords : IPluginRecords
{
    private const int LookupBatch = 500;

    private readonly IDbContextFactory<OadmDbContext> _dbFactory;
    private readonly string _pluginId;

    public DbPluginRecords(IDbContextFactory<OadmDbContext> dbFactory, string pluginId)
    {
        SettingKeys.ValidatePluginId(pluginId);
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _pluginId = pluginId;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct)
    {
        PluginRecordRules.ValidateCollection(collection);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.PluginRecords.AsNoTracking()
            .Where(r => r.PluginId == _pluginId && r.Collection == collection)
            .ToDictionaryAsync(r => r.Key, r => r.ValueJson, StringComparer.Ordinal, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string collection, string key, CancellationToken ct)
    {
        PluginRecordRules.ValidateCollection(collection);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.PluginRecords.AsNoTracking()
            .Where(r => r.PluginId == _pluginId && r.Collection == collection && r.Key == key)
            .Select(r => r.ValueJson)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
    {
        PluginRecordRules.Validate(collection, records);
        if (records.Count == 0)
        {
            return;
        }

        // The last value of a key counts.
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in records)
        {
            wanted[key] = value;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var batch in wanted.Keys.Chunk(LookupBatch))
        {
            var existing = await db.PluginRecords
                .Where(r => r.PluginId == _pluginId && r.Collection == collection && batch.Contains(r.Key))
                .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, ct).ConfigureAwait(false);
            foreach (var key in batch)
            {
                var value = wanted[key];
                if (existing.TryGetValue(key, out var row))
                {
                    if (value is null)
                    {
                        db.PluginRecords.Remove(row);
                    }
                    else
                    {
                        row.ValueJson = value;
                    }
                }
                else if (value is not null)
                {
                    db.PluginRecords.Add(new PluginRecord { PluginId = _pluginId, Collection = collection, Key = key, ValueJson = value });
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(string collection, CancellationToken ct)
    {
        PluginRecordRules.ValidateCollection(collection);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.PluginRecords.Where(r => r.PluginId == _pluginId && r.Collection == collection).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public override string ToString() => $"PluginRecords({_pluginId})";
}
