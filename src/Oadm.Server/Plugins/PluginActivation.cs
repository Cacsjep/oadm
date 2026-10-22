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
    /// Throws <see cref="KeyNotFoundException"/> for an unknown package, <see cref="InvalidOperationException"/> for one
    /// that is always on. <paramref name="ct"/> only covers the wait for an earlier change: once the registry has the new
    /// state, storing it and starting or stopping the core plugins run to the end, so a cancelled call never leaves the
    /// registry, the stored choice and the running plugins disagreeing. A choice that cannot be stored is undone.
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
            var before = registry.IsEnabled(packageId);
            var states = registry.SetEnabled(packageId, enabled);
            try
            {
                await settings.SetJsonAsync(SettingKey, JsonSerializer.Serialize(states), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                registry.SetEnabled(packageId, before);
                throw;
            }

            await host.SyncEnabledAsync(CancellationToken.None).ConfigureAwait(false);
            LogChanged(logger, packageId, enabled ? "on" : "off");
        }
        finally
        {
            _gate.Release();
        }

        return registry.Packages.First(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts and stops the PRO packages when the OADM PRO license changes (imported, removed, ran out): the registry gates
    /// them, this applies it to the running core plugins. The administrator's choice stays stored.
    /// </summary>
    public void FollowPro(Sdk.Licensing.IProLicense pro)
    {
        ArgumentNullException.ThrowIfNull(pro);
        var active = pro.IsActive;
        pro.Changed += (_, _) =>
        {
            var now = pro.IsActive;
            if (now == active)
            {
                return;
            }

            active = now;
            registry.ProChanged();
            _ = SyncAsync(now);
        };
    }

    private async Task SyncAsync(bool proActive)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await host.SyncEnabledAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var package in registry.Packages.Where(p => p.Origin.Pro))
            {
                LogProChanged(logger, package.Id, package.Enabled ? "on" : "off", proActive ? "active" : "not active");
            }
        }
#pragma warning disable CA1031 // A failed sync is logged; the next change or restart applies it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProSyncFailed(logger, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} is {State}: OADM PRO is {Pro}")]
    private static partial void LogProChanged(ILogger logger, string packageId, string state, string pro);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The PRO plugins could not follow the license change")]
    private static partial void LogProSyncFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored plugin states cannot be read; every plugin uses its default")]
    private static partial void LogUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} is turned off")]
    private static partial void LogOff(ILogger logger, string packageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PackageId} turned {State}")]
    private static partial void LogChanged(ILogger logger, string packageId, string state);
}
