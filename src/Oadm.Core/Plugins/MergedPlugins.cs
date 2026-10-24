using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Core.Settings;

namespace Oadm.Core.Plugins;

/// <summary>
/// Plugin packages of OADM 4.1 that are parts of another package now (docs/spec/pki.md "One plugin"): the EST server
/// (<c>oadm.est</c>) and the 802.1X server (<c>oadm.radius</c>) are core plugins of PKI (<c>oadm.pki</c>) with the same
/// core plugin ids, so their settings, records, data folders, permissions and events keep their keys. A plugin folder of
/// such a package left from 4.1 is not loaded (<see cref="PluginLoader"/>, the client's loader keeps its own copy of the
/// list); the package's on/off choice becomes the server's own stored choice once (<see cref="TakeOverAsync"/>).
/// </summary>
public static partial class MergedPlugins
{
    /// <summary>The package the merged ones belong to now.</summary>
    public const string PkiPackageId = "oadm.pki";

    /// <summary>The log line of a skipped plugin folder.</summary>
    public const string MergedText = "Merged into PKI";

    /// <summary>
    /// The plugin setting (in the namespace of the merged core plugin) that holds the server's Turn on / Turn off choice:
    /// JSON <c>true</c> or <c>false</c>, none = off. The servers read it under the same name (<c>ServerSwitch.SettingKey</c>).
    /// </summary>
    public const string ServerOnSetting = "serverOn";

    /// <summary>The ids of the merged packages (= the ids of their core plugins).</summary>
    public static IReadOnlyList<string> Ids { get; } = ["oadm.est", "oadm.radius"];

    /// <summary>The package <paramref name="packageId"/> was merged into; null for every other package.</summary>
    public static string? MergedInto(string? packageId) =>
        packageId is not null && Ids.Contains(packageId, StringComparer.OrdinalIgnoreCase) ? PkiPackageId : null;

    /// <summary>
    /// Takes over the on/off choices of OADM 4.1 (decision 2026-10-24): a merged package's entry in
    /// <paramref name="enabledKey"/> (JSON object package id -> on) becomes <see cref="ServerOnSetting"/> of its core
    /// plugin, unless that is stored already (a choice made since wins), and leaves the object. Idempotent and safe to stop at
    /// any point: the setting is written before the entry goes. Returns the ids taken over.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TakeOverAsync(ServerSettingsStore settings, string enabledKey, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledKey);
        ArgumentNullException.ThrowIfNull(logger);
        var json = await settings.GetJsonAsync(enabledKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        Dictionary<string, bool>? states;
        try
        {
            states = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
        }
        catch (JsonException)
        {
            return []; // unreadable: the activation logs it and every plugin uses its default
        }

        if (states is null)
        {
            return [];
        }

        var taken = new List<string>();
        foreach (var (id, on) in states.Where(s => MergedInto(s.Key) is not null).ToList())
        {
            var key = SettingKeys.PluginKey(Ids.First(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase)), ServerOnSetting);
            if (await settings.GetJsonAsync(key, ct).ConfigureAwait(false) is null)
            {
                await settings.SetJsonAsync(key, on ? "true" : "false", ct).ConfigureAwait(false);
                LogTakenOver(logger, id, on ? "on" : "off");
            }

            states.Remove(id);
            taken.Add(id);
        }

        if (taken.Count > 0)
        {
            await settings.SetJsonAsync(enabledKey, JsonSerializer.Serialize(states), ct).ConfigureAwait(false);
        }

        return taken;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId}: Merged into PKI, its server stays {State}")]
    private static partial void LogTakenOver(ILogger logger, string packageId, string state);
}
