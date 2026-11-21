using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Settings;

namespace Oadm.Core.Plugins;

/// <summary>
/// Bundled plugins a release no longer ships (docs/spec/plugin-removal.md): the server remembers which ids the bundled root
/// had (<see cref="SettingKey"/>), and at the next start every id of that set it no longer knows loses all its state once:
/// its on/off choice, settings, records, data folder, role permissions and a start page of it. Nothing at a start with any
/// plugin load error; never an id that is only turned off, PRO-locked, installed in the data folder or an add-on.
/// </summary>
public sealed partial class RemovedPlugins(
    IDbContextFactory<OadmDbContext> dbFactory,
    ServerSettingsStore settings,
    RoleStore roles,
    AuditLog audit,
    string pluginDataRoot,
    string enabledKey,
    ILogger logger,
    string? version = null,
    Func<string, string, bool>? isOlder = null)
{
    /// <summary>JSON array of the package and plugin ids the previous start loaded from the bundled root.</summary>
    public const string SettingKey = "Plugins.Bundled";

    /// <summary>The OADM version that stored <see cref="SettingKey"/>: a set of a newer OADM (a restored backup) is never cleaned.</summary>
    public const string VersionKey = "Plugins.BundledVersion";

    /// <summary>The audit entry, log line and restore report line of a deleted id.</summary>
    public static string DeletedText(string id) => $"Plugin {id} is no longer part of OADM: its settings and data were deleted.";

    /// <summary>
    /// Deletes the state of every id of the stored set that is not in <paramref name="known"/>, then stores
    /// <paramref name="bundled"/>. <paramref name="bundled"/> null (no bundled root with a manifest: a development run) or
    /// <paramref name="loadErrors"/> change nothing, nor does a set stored by a newer OADM than <c>version</c> (<see cref="VersionKey"/>;
    /// none stored or no <c>version</c>: cleaned as usual). Idempotent: every step deletes what is there. Returns the ids whose state
    /// was deleted.
    /// </summary>
    public async Task<IReadOnlyList<string>> CleanAsync(IReadOnlyCollection<string>? bundled, IReadOnlySet<string> known, bool loadErrors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (bundled is null || loadErrors)
        {
            return [];
        }

        // A set a newer OADM stored (a backup of it restored here) names plugins this version never had: keep all and the set.
        var storedVersion = version is null || isOlder is null ? null : await settings.GetAsync<string>(VersionKey, ct).ConfigureAwait(false);
        if (storedVersion is not null && isOlder!(version!, storedVersion))
        {
            return [];
        }

        var previous = await ReadSetAsync(ct).ConfigureAwait(false);
        var gone = previous
            .Where(id => !known.Contains(id) && MergedPlugins.MergedInto(id) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
        var deleted = new List<string>();
        var again = new List<string>(); // a data folder that could not go: the id stays in the set, the next start tries again
        if (gone.Count > 0)
        {
            var touched = new HashSet<string>(await roles.ForgetPluginsAsync(gone, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
            foreach (var id in gone)
            {
                var (any, complete) = await DeleteStateAsync(id, ct).ConfigureAwait(false);
                if (!complete)
                {
                    again.Add(id);
                }

                if (any || touched.Contains(id))
                {
                    deleted.Add(id);
                    LogDeleted(logger, id);
                    await audit.WriteAsync("system", string.Empty, AuditActions.PluginDataDeleted, id, DeletedText(id), ct).ConfigureAwait(false);
                }
            }
        }

        var now = bundled.Concat(again).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (!now.SequenceEqual(previous, StringComparer.OrdinalIgnoreCase))
        {
            await settings.SetAsync(SettingKey, now, ct).ConfigureAwait(false);
        }

        if (version is not null && isOlder is not null && !string.Equals(storedVersion, version, StringComparison.Ordinal))
        {
            await settings.SetAsync(VersionKey, version, ct).ConfigureAwait(false);
        }

        return deleted;
    }

    private async Task<string[]> ReadSetAsync(CancellationToken ct)
    {
        var json = await settings.GetJsonAsync(SettingKey, ct).ConfigureAwait(false);
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The on/off choice, the settings, the records and the data folder of one id. Any: something was there; complete: nothing
    /// of it is left.
    /// </summary>
    private async Task<(bool Any, bool Complete)> DeleteStateAsync(string id, CancellationToken ct)
    {
        var any = false;
        var json = await settings.GetJsonAsync(enabledKey, ct).ConfigureAwait(false);
        Dictionary<string, bool>? states = null;
        try
        {
            states = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
        }
        catch (JsonException)
        {
            // An unreadable choice list is not ours to fix.
        }

        if (states is not null && states.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) is { } key)
        {
            states.Remove(key);
            await settings.SetJsonAsync(enabledKey, JsonSerializer.Serialize(states), ct).ConfigureAwait(false);
            any = true;
        }

        if (!id.Contains(':', StringComparison.Ordinal))
        {
            var prefix = SettingKeys.PluginPrefix + id + ":";
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            any |= await db.Settings.Where(s => s.Key.StartsWith(prefix)).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
            any |= await db.PluginRecords.Where(r => r.PluginId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
        }

        var folder = Path.Combine(pluginDataRoot, CorePluginHost.SafeFolderName(id));
        if (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                any = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFolderKept(logger, folder, ex.Message);
                return (any, false);
            }
        }

        return (any, true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} is no longer part of OADM: its settings and data were deleted.")]
    private static partial void LogDeleted(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The data folder {Folder} of a plugin no longer in OADM could not be deleted: {Reason}")]
    private static partial void LogFolderKept(ILogger logger, string folder, string reason);
}
