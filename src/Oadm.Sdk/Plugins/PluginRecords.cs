using System.Text.Json;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Records of a plugin in the server database (<see cref="ICorePluginContext.Records"/>): named collections of JSON values
/// by key, one table row per record. For data that grows with the devices (a record per device or certificate), where
/// one <see cref="IPluginSettings"/> value would be rewritten whole on every change. A plugin sees only its own records.
/// </summary>
public interface IPluginRecords
{
    /// <summary>Every record of <paramref name="collection"/>: key and JSON.</summary>
    Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct);

    /// <summary>
    /// Writes several records of <paramref name="collection"/> in one transaction; a null value deletes its key. Keys: 1 to
    /// 255 characters; values: valid JSON.
    /// </summary>
    Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct);

    /// <summary>Deletes every record of <paramref name="collection"/>.</summary>
    Task ClearAsync(string collection, CancellationToken ct);
}

/// <summary>
/// <see cref="IPluginRecords"/> in memory (tests, hosts without a database): the same checks as the database, lost when the
/// process ends.
/// </summary>
public sealed class MemoryPluginRecords : IPluginRecords
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _collections = new(StringComparer.Ordinal);
    private int _written;

    /// <summary>Every write of <see cref="SetManyAsync"/> so far (tests check that only changed records are written).</summary>
    public int WrittenRecords => Volatile.Read(ref _written);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct)
    {
        PluginRecordRules.ValidateCollection(collection);
        lock (_gate)
        {
            IReadOnlyDictionary<string, string> list = _collections.TryGetValue(collection, out var records)
                ? new Dictionary<string, string>(records, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
    {
        PluginRecordRules.Validate(collection, records);
        lock (_gate) // one batch at a time, all or nothing seen by readers, like the database's transaction
        {
            if (!_collections.TryGetValue(collection, out var target))
            {
                _collections[collection] = target = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var (key, value) in records)
            {
                if (value is null)
                {
                    target.Remove(key);
                }
                else
                {
                    target[key] = value;
                }
            }

            _written += records.Count;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(string collection, CancellationToken ct)
    {
        PluginRecordRules.ValidateCollection(collection);
        lock (_gate)
        {
            _collections.Remove(collection);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The checks every <see cref="IPluginRecords"/> applies.</summary>
public static class PluginRecordRules
{
    /// <summary>The longest collection name.</summary>
    public const int MaxCollectionLength = 64;

    /// <summary>The longest key.</summary>
    public const int MaxKeyLength = 255;

    /// <summary>Throws <see cref="ArgumentException"/> for an empty or too long collection name.</summary>
    public static void ValidateCollection(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (collection.Length > MaxCollectionLength)
        {
            throw new ArgumentException($"A collection name has at most {MaxCollectionLength} characters.", nameof(collection));
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> for a bad collection, an empty or too long key, or a value that is not JSON.</summary>
    public static void Validate(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records)
    {
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(records);
        foreach (var (key, value) in records)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            {
                throw new ArgumentException($"A key has 1 to {MaxKeyLength} characters.", nameof(records));
            }

            if (value is null)
            {
                continue;
            }

            try
            {
                using var _ = JsonDocument.Parse(value);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"The value of '{key}' is not valid JSON.", nameof(records), ex);
            }
        }
    }
}
