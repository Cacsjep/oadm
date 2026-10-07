using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;

namespace Oadm.Core.Settings;

/// <summary>
/// Server settings over the Settings table. Values are JSON. Keys that were never written return
/// their typed default (see <see cref="ServerSettings.Defaults"/>); defaults are not stored.
/// </summary>
public sealed class ServerSettingsStore(IDbContextFactory<OadmDbContext> dbFactory)
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _defaults = new(BuildDefaults);

    /// <summary>Raised after every successful write or delete.</summary>
    public event EventHandler<SettingChangedEventArgs>? Changed;

    /// <summary>Default JSON values of the known server keys.</summary>
    public IReadOnlyDictionary<string, string> Defaults => _defaults.Value;

    /// <summary>Stored JSON value, else the default for known keys, else null.</summary>
    public async Task<string?> GetJsonAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        return row?.ValueJson ?? Defaults.GetValueOrDefault(key);
    }

    /// <summary>Writes a JSON value. Null deletes the key (known keys fall back to their default).</summary>
    /// <exception cref="ArgumentException">Value is not valid JSON or out of range for a known key.</exception>
    public async Task SetJsonAsync(string key, string? valueJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (valueJson is not null)
        {
            Validate(key, valueJson);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await SettingRows.WriteAsync(db, key, valueJson, ct).ConfigureAwait(false);
        Changed?.Invoke(this, new SettingChangedEventArgs(key, valueJson));
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        var json = await GetJsonAsync(key, ct).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct) =>
        SetJsonAsync(key, JsonSerializer.Serialize(value), ct);

    /// <summary>Removes a stored value so the default applies again.</summary>
    public Task ResetAsync(string key, CancellationToken ct) => SetJsonAsync(key, null, ct);

    /// <summary>Typed snapshot of all Goal 1 settings.</summary>
    public async Task<ServerSettings> GetServerSettingsAsync(CancellationToken ct)
    {
        var defaults = ServerSettings.Defaults;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Settings.AsNoTracking()
            .Where(s => !s.Key.StartsWith(SettingKeys.PluginPrefix))
            .ToDictionaryAsync(s => s.Key, s => s.ValueJson, ct).ConfigureAwait(false);

        return new ServerSettings(
            Read(rows, SettingKeys.PollingIntervalSeconds, defaults.PollingIntervalSeconds),
            Read(rows, SettingKeys.ScanParallelism, defaults.ScanParallelism),
            Read(rows, SettingKeys.ScanTimeoutMs, defaults.ScanTimeoutMs),
            Read(rows, SettingKeys.ServerName, defaults.ServerName),
            Read(rows, SettingKeys.ListenUrl, defaults.ListenUrl),
            Read(rows, SettingKeys.PollingFullRefreshMinutes, defaults.FullRefreshMinutes),
            Read(rows, SettingKeys.DevicesUseHostName, defaults.UseHostName),
            Read(rows, SettingKeys.DiscoveryZeroConfSeconds, defaults.ZeroConfSeconds));
    }

    /// <summary>Validates and writes all Goal 1 settings in one transaction.</summary>
    public async Task<ServerSettings> SetServerSettingsAsync(ServerSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = new Dictionary<string, string>
        {
            [SettingKeys.PollingIntervalSeconds] = JsonSerializer.Serialize(settings.PollingIntervalSeconds),
            [SettingKeys.ScanParallelism] = JsonSerializer.Serialize(settings.ScanParallelism),
            [SettingKeys.ScanTimeoutMs] = JsonSerializer.Serialize(settings.ScanTimeoutMs),
            [SettingKeys.ServerName] = JsonSerializer.Serialize(settings.ServerName),
            [SettingKeys.ListenUrl] = JsonSerializer.Serialize(settings.ListenUrl),
            [SettingKeys.PollingFullRefreshMinutes] = JsonSerializer.Serialize(settings.FullRefreshMinutes),
            [SettingKeys.DevicesUseHostName] = JsonSerializer.Serialize(settings.UseHostName),
            [SettingKeys.DiscoveryZeroConfSeconds] = JsonSerializer.Serialize(settings.ZeroConfSeconds),
        };
        foreach (var (key, json) in values)
        {
            Validate(key, json);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var (key, json) in values)
            {
                await SettingRows.WriteAsync(db, key, json, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        foreach (var (key, json) in values)
        {
            Changed?.Invoke(this, new SettingChangedEventArgs(key, json));
        }

        return settings;
    }

    private static T Read<T>(Dictionary<string, string> rows, string key, T fallback)
    {
        if (!rows.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static void Validate(string key, string valueJson)
    {
        JsonElement value;
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            value = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Value of '{key}' is not valid JSON.", nameof(valueJson), ex);
        }

        switch (key)
        {
            case SettingKeys.PollingIntervalSeconds:
                RequireInt(key, value, 5, 86_400);
                break;
            case SettingKeys.PollingFullRefreshMinutes:
                RequireInt(key, value, ServerSettings.MinFullRefreshMinutes, ServerSettings.MaxFullRefreshMinutes);
                break;
            case SettingKeys.UploadsMaxMegabytes:
                RequireInt(key, value, 1, ServerSettings.MaxUploadsMaxMegabytes);
                break;
            case SettingKeys.UploadsRetentionHours:
                RequireInt(key, value, 1, ServerSettings.MaxUploadsRetentionHours);
                break;
            case SettingKeys.ScanParallelism:
                RequireInt(key, value, 1, 1024);
                break;
            case SettingKeys.ScanTimeoutMs:
                RequireInt(key, value, 100, 60_000);
                break;
            case SettingKeys.DiscoveryZeroConfSeconds:
                RequireInt(key, value, ServerSettings.MinZeroConfSeconds, ServerSettings.MaxZeroConfSeconds);
                break;
            case SettingKeys.ServerName:
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    throw new ArgumentException($"'{key}' must be a non-empty string.", nameof(valueJson));
                }

                break;
            case SettingKeys.DevicesUseHostName:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException($"'{key}' must be true or false.", nameof(valueJson));
                }

                break;
            case SettingKeys.ListenUrl:
                if (value.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException($"'{key}' must be an absolute http or https URL.", nameof(valueJson));
                }

                break;
        }
    }

    private static void RequireInt(string key, JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{key}' must be an integer between {min} and {max}."),
                nameof(value));
        }
    }

    private static Dictionary<string, string> BuildDefaults()
    {
        var d = ServerSettings.Defaults;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SettingKeys.PollingIntervalSeconds] = JsonSerializer.Serialize(d.PollingIntervalSeconds),
            [SettingKeys.ScanParallelism] = JsonSerializer.Serialize(d.ScanParallelism),
            [SettingKeys.ScanTimeoutMs] = JsonSerializer.Serialize(d.ScanTimeoutMs),
            [SettingKeys.ServerName] = JsonSerializer.Serialize(d.ServerName),
            [SettingKeys.ListenUrl] = JsonSerializer.Serialize(d.ListenUrl),
            [SettingKeys.PollingFullRefreshMinutes] = JsonSerializer.Serialize(d.FullRefreshMinutes),
            [SettingKeys.UploadsMaxMegabytes] = JsonSerializer.Serialize(ServerSettings.DefaultUploadsMaxMegabytes),
            [SettingKeys.UploadsRetentionHours] = JsonSerializer.Serialize(ServerSettings.DefaultUploadsRetentionHours),
            [SettingKeys.DevicesUseHostName] = JsonSerializer.Serialize(d.UseHostName),
            [SettingKeys.DiscoveryZeroConfSeconds] = JsonSerializer.Serialize(d.ZeroConfSeconds),
        };
    }
}

/// <summary>Upsert/delete helper shared by server and plugin settings.</summary>
internal static class SettingRows
{
    public static async Task WriteAsync(OadmDbContext db, string key, string? valueJson, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (valueJson is null)
        {
            if (row is not null)
            {
                db.Settings.Remove(row);
            }
        }
        else if (row is null)
        {
            db.Settings.Add(new Setting { Key = key, ValueJson = valueJson });
        }
        else
        {
            row.ValueJson = valueJson;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
