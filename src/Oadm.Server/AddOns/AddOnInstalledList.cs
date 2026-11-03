using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Core.Plugins;
using Oadm.Core.Settings;

namespace Oadm.Server.AddOns;

/// <summary>An add-on in the server's list of installed add-ons.</summary>
/// <param name="Id">The add-on's id (its folder under <c>addons/</c>).</param>
/// <param name="Version">The OADM release it was downloaded from ("4.11.0").</param>
/// <param name="Removed">Removed: its folder goes at the next server start.</param>
public sealed record InstalledAddOn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("removed")] bool Removed = false);

/// <summary>
/// The list of installed add-ons (docs/spec/add-ons.md "Server": a server setting, so a backup holds the list and a restore
/// downloads the files again).
/// </summary>
public interface IAddOnListStore
{
    Task<IReadOnlyList<InstalledAddOn>> ReadAsync(CancellationToken ct);

    Task WriteAsync(IReadOnlyList<InstalledAddOn> list, CancellationToken ct);
}

/// <summary>The list in the server setting <see cref="SettingKey"/> (JSON array).</summary>
public sealed class SettingsAddOnListStore(ServerSettingsStore settings) : IAddOnListStore
{
    public const string SettingKey = "AddOns.Installed";

    public async Task<IReadOnlyList<InstalledAddOn>> ReadAsync(CancellationToken ct)
    {
        var json = await settings.GetJsonAsync(SettingKey, ct).ConfigureAwait(false);
        return Parse(json);
    }

    public Task WriteAsync(IReadOnlyList<InstalledAddOn> list, CancellationToken ct) =>
        settings.SetJsonAsync(SettingKey, JsonSerializer.Serialize(list), ct);

    /// <summary>The list of a stored value; an unreadable value is an empty list (the folders are found again at start).</summary>
    public static IReadOnlyList<InstalledAddOn> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<InstalledAddOn>>(json) ?? []).Where(a => !string.IsNullOrWhiteSpace(a?.Id))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Turns an add-on off and waits until a package loaded while the server runs is followed (the Turn on and off path). The
/// load itself raises <see cref="PluginRegistry.PackageAdded"/> (<see cref="PluginLoader.LoadPackage"/>), never the installer.
/// </summary>
public interface IAddOnActivation
{
    /// <summary>Turns the package off and stores that (an installed add-on starts off; Remove turns it off at once).</summary>
    Task TurnOffAsync(string packageId, CancellationToken ct);

    /// <summary>
    /// Waits until the roles, the license and the core plugins follow the package just loaded (its stored choice: a package
    /// fetched after an update starts its core plugins when it is on). Never faults.
    /// </summary>
    Task PackageFollowedAsync(string packageId, CancellationToken ct);
}

/// <summary><see cref="IAddOnActivation"/> over <see cref="Plugins.PluginActivation"/>, the Plugins page's Turn on and off.</summary>
public sealed class PluginActivationAddOns(Plugins.PluginActivation activation, PluginRegistry registry) : IAddOnActivation
{
    public async Task TurnOffAsync(string packageId, CancellationToken ct)
    {
        if (registry.HasPackage(packageId))
        {
            await activation.SetEnabledAsync(packageId, false, ct).ConfigureAwait(false);
        }
    }

    public Task PackageFollowedAsync(string packageId, CancellationToken ct) => activation.AddedPackagesFollowed.WaitAsync(ct);
}
