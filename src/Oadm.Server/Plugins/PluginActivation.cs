using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Core.Plugins;
using Oadm.Core.Settings;

namespace Oadm.Server.Plugins;

/// <summary>
/// Which plugin packages are on (Settings page, Plugins). The administrator's choices are stored in the server
/// setting <see cref="SettingKey"/> (JSON object package id -> on); packages without a choice use their manifest's
/// <c>enabledByDefault</c> (the Hardening scan is off by default). A change applies at once: the core plugins of a
/// package that was turned off stop, its tasks leave the context menu, and turning it on starts them again.
/// </summary>
public sealed partial class PluginActivation(
    PluginRegistry registry,
    CorePluginHost host,
    ServerSettingsStore settings,
    ILogger<PluginActivation> logger) : IDisposable
{
    public const string SettingKey = "Plugins.Enabled";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Applies the stored choices to the registry (server start, before the core plugins start).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var json = await settings.GetJsonAsync(SettingKey, ct).ConfigureAwait(false);
        Dictionary<string, bool>? states = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                states = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            }
            catch (JsonException ex)
            {
                LogUnreadable(logger, ex);
            }
        }

        registry.ApplyEnabledStates(states ?? []);
        foreach (var package in registry.Packages.Where(p => !p.Enabled))
        {
            LogOff(logger, package.Id);
        }
    }

    /// <summary>
    /// Turns a package on or off, stores the choice and starts or stops its core plugins.
    /// Throws <see cref="KeyNotFoundException"/> for an unknown package.
    /// </summary>
    public async Task<PluginPackageState> SetEnabledAsync(string packageId, bool enabled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (!registry.HasPackage(packageId))
        {
            throw new KeyNotFoundException($"Unknown plugin '{packageId}'.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var states = registry.SetEnabled(packageId, enabled);
            await settings.SetJsonAsync(SettingKey, JsonSerializer.Serialize(states), ct).ConfigureAwait(false);
            await host.SyncEnabledAsync(ct).ConfigureAwait(false);
            LogChanged(logger, packageId, enabled ? "on" : "off");
        }
        finally
        {
            _gate.Release();
        }

        return registry.Packages.First(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored plugin states cannot be read; every plugin uses its default")]
    private static partial void LogUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} is turned off")]
    private static partial void LogOff(ILogger logger, string packageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} turned {State}")]
    private static partial void LogChanged(ILogger logger, string packageId, string state);
}
