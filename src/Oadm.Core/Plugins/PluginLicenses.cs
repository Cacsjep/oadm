using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;

namespace Oadm.Core.Plugins;

/// <summary>The live license of a plugin package for the plugin's contexts.</summary>
public interface IPluginLicenseSource
{
    /// <summary>The license of the package <paramref name="packageId"/>; null for an unknown package.</summary>
    IPluginLicense? LicenseOf(string packageId);
}

/// <summary>
/// Licenses of commercial plugins (plugin.json <c>license</c>). The server ID (setting <see cref="ServerIdKey"/>, a random
/// id made at the first start and kept in the database, so a restored backup keeps its licenses), the licenses entered on
/// the Plugins page (<see cref="LicensesKey"/>: package id -> license text) and the day each plugin's trial started
/// (<see cref="TrialsKey"/>, the first start that loaded it). Every package gets one live <see cref="IPluginLicense"/>;
/// <see cref="Refresh"/> (hourly) moves expired licenses and trials on. OADM only checks; plugins decide.
/// </summary>
public sealed partial class PluginLicenses : IPluginLicenseSource, IDisposable
{
    public const string ServerIdKey = "Server.Id";
    public const string LicensesKey = "Plugins.Licenses";
    public const string TrialsKey = "Plugins.TrialStarted";

    private readonly PluginRegistry _registry;
    private readonly ServerSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, LiveLicense> _live = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateOnly> _trials = new(StringComparer.OrdinalIgnoreCase);

    public PluginLicenses(PluginRegistry registry, ServerSettingsStore settings, TimeProvider? time = null, ILogger<PluginLicenses>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);
        _registry = registry;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>This server's ID, the one licenses name; empty until <see cref="InitializeAsync"/>.</summary>
    public string ServerId { get; private set; } = string.Empty;

    /// <summary>
    /// Reads (or creates) the server ID, the stored licenses and trials, starts the trial of every licensed package seen
    /// for the first time, checks every package and hands itself to the registry (server start, before the plugins start).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var id = await _settings.GetAsync<string>(ServerIdKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("D");
            await _settings.SetAsync(ServerIdKey, id, ct).ConfigureAwait(false);
        }

        ServerId = id.Trim().ToLowerInvariant();
        _texts = await ReadAsync<string>(LicensesKey, ct).ConfigureAwait(false);
        _trials = await ReadAsync<DateOnly>(TrialsKey, ct).ConfigureAwait(false);
        var today = Today();
        var started = false;
        foreach (var package in _registry.Packages.Where(p => p.Origin.License is { TrialDays: > 0 }))
        {
            started |= _trials.TryAdd(package.Id, today);
        }

        if (started)
        {
            await _settings.SetJsonAsync(TrialsKey, JsonSerializer.Serialize(_trials), ct).ConfigureAwait(false);
        }

        _registry.Licenses = this;
        Refresh();
    }

    public IPluginLicense? LicenseOf(string packageId)
    {
        if (!_registry.HasPackage(packageId))
        {
            return null;
        }

        return _live.GetOrAdd(packageId, id => new LiveLicense(Check(id)));
    }

    /// <summary>The checked license of a package (the Plugins page).</summary>
    public PluginLicenseInfo Info(string packageId) => LicenseOf(packageId)?.Current ?? PluginLicenseInfo.NotRequired;

    /// <summary>
    /// Checks and stores a license for a package; it applies at once. Throws <see cref="KeyNotFoundException"/> for an
    /// unknown package, <see cref="InvalidOperationException"/> for a package that needs no license and
    /// <see cref="ArgumentException"/> (the reason as message) for a license that is not valid, for another server or
    /// expired; nothing is stored then.
    /// </summary>
    public async Task<PluginLicenseInfo> SetAsync(string packageId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var manifest = ManifestOf(packageId);
        var checkedLicense = LicenseFile.Verify(text, manifest.PublicKey, packageId, ServerId, Today());
        if (checkedLicense.Status != LicenseStatus.Valid)
        {
            throw new ArgumentException(checkedLicense.Reason);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var texts = new Dictionary<string, string>(_texts, StringComparer.OrdinalIgnoreCase) { [packageId] = text };
            await _settings.SetJsonAsync(LicensesKey, JsonSerializer.Serialize(texts), CancellationToken.None).ConfigureAwait(false);
            _texts = texts;
        }
        finally
        {
            _write.Release();
        }

        LogEntered(_logger, packageId, checkedLicense.Data!.Customer);
        return Update(packageId);
    }

    /// <summary>Removes the license of a package; the plugin falls back to its trial or to no license.</summary>
    public async Task<PluginLicenseInfo> RemoveAsync(string packageId, CancellationToken ct)
    {
        ManifestOf(packageId);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_texts.ContainsKey(packageId))
            {
                var texts = new Dictionary<string, string>(_texts, StringComparer.OrdinalIgnoreCase);
                texts.Remove(packageId);
                await _settings.SetJsonAsync(LicensesKey, JsonSerializer.Serialize(texts), CancellationToken.None).ConfigureAwait(false);
                _texts = texts;
            }
        }
        finally
        {
            _write.Release();
        }

        LogRemoved(_logger, packageId);
        return Update(packageId);
    }

    /// <summary>Checks every package again (a license or a trial may have run out) and raises Changed where it changed.</summary>
    public void Refresh()
    {
        foreach (var package in _registry.Packages)
        {
            Update(package.Id);
        }
    }

    public void Dispose() => _write.Dispose();

    private PluginLicenseInfo Update(string packageId)
    {
        var info = Check(packageId);
        var live = _live.GetOrAdd(packageId, _ => new LiveLicense(info));
        if (live.Set(info) && info.Status is not (LicenseStatus.Valid or LicenseStatus.NotRequired or LicenseStatus.Trial))
        {
            LogNotValid(_logger, packageId, info.Reason);
        }

        return info;
    }

    private PluginLicenseInfo Check(string packageId)
    {
        var package = _registry.Packages.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
        if (package?.Origin.License is not { } manifest)
        {
            return PluginLicenseInfo.NotRequired;
        }

        var today = Today();
        if (_texts.TryGetValue(packageId, out var text))
        {
            var info = LicenseFile.Verify(text, manifest.PublicKey, packageId, ServerId, today);
            if (info.Status == LicenseStatus.Valid || manifest.TrialDays <= 0 || !_trials.TryGetValue(packageId, out var started))
            {
                return info;
            }

            // A license that is no longer valid while the trial still runs: the trial counts, with the license's reason.
            var ends = started.AddDays(manifest.TrialDays - 1);
            return ends >= today ? new PluginLicenseInfo(LicenseStatus.Trial, info.Reason, info.Data, ends) : info;
        }

        if (manifest.TrialDays > 0 && _trials.TryGetValue(packageId, out var start))
        {
            var ends = start.AddDays(manifest.TrialDays - 1);
            return ends >= today
                ? new PluginLicenseInfo(LicenseStatus.Trial, $"Trial until {LicenseFile.Day(ends)}.", null, ends)
                : new PluginLicenseInfo(LicenseStatus.TrialEnded, $"The trial ended on {LicenseFile.Day(ends)}. Enter a license.", null, ends);
        }

        return new PluginLicenseInfo(LicenseStatus.Missing, "No license. Enter a license.");
    }

    private PluginLicenseManifest ManifestOf(string packageId)
    {
        var package = _registry.Packages.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown plugin '{packageId}'.");
        return package.Origin.License ?? throw new InvalidOperationException($"{package.Origin.DisplayName} needs no license.");
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private async Task<Dictionary<string, T>> ReadAsync<T>(string key, CancellationToken ct)
    {
        var json = await _settings.GetJsonAsync(key, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, T>>(json);
                if (read is not null)
                {
                    return new Dictionary<string, T>(read, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (JsonException ex)
            {
                LogUnreadable(_logger, ex, key);
            }
        }

        return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "License for plugin {PackageId} entered (customer {Customer})")]
    private static partial void LogEntered(ILogger logger, string packageId, string customer);

    [LoggerMessage(Level = LogLevel.Information, Message = "License for plugin {PackageId} removed")]
    private static partial void LogRemoved(ILogger logger, string packageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PackageId} has no valid license: {Reason}")]
    private static partial void LogNotValid(ILogger logger, string packageId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored setting {Key} cannot be read; it starts empty")]
    private static partial void LogUnreadable(ILogger logger, Exception ex, string key);

    /// <summary>One package's license; <see cref="Set"/> replaces it and raises Changed when it differs.</summary>
    private sealed class LiveLicense(PluginLicenseInfo initial) : IPluginLicense
    {
        private PluginLicenseInfo _current = initial;
        private bool _reported;

        public PluginLicenseInfo Current => Volatile.Read(ref _current);

        public event EventHandler? Changed;

        /// <summary>True the first time and whenever the state changed (for the log).</summary>
        public bool Set(PluginLicenseInfo info)
        {
            var before = Interlocked.Exchange(ref _current, info);
            var same = before.Status == info.Status && before.Reason == info.Reason && Equals(before.TrialEnds, info.TrialEnds);
            if (same)
            {
                var first = !_reported;
                _reported = true;
                return first;
            }

            _reported = true;
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A plugin's handler never breaks the license check.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            return true;
        }
    }
}
