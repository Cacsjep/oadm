using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;

namespace Oadm.Core.Licensing;

/// <summary>
/// The server's OADM PRO license (docs/spec/licensing.md): a license key in the plugin license format
/// (<see cref="ProLicenseFile"/>) signed with one of <see cref="ProLicenseKeys"/>, for this
/// server's ID, or the 30-day trial OADM starts itself once per server (<see cref="StartTrialAsync"/>). The imported key is
/// kept in the setting <see cref="SettingKey"/>, the trial in <see cref="TrialSettingKey"/> and in the install file of the
/// data folder (<see cref="InstallFileName"/>, never in a backup); both are checked again at start and by
/// <see cref="Refresh"/> (hourly, so an expiry reaches the plugins the same day). An active key wins over the trial. OADM
/// itself never depends on it.
/// </summary>
public sealed partial class ProLicense : IProLicense, IDisposable
{
    /// <summary>The setting that holds the imported license key.</summary>
    public const string SettingKey = "License.Pro";

    /// <summary>The setting that records the trial: <see cref="TrialRecord"/> as JSON (server ID, install ID, first day).</summary>
    public const string TrialSettingKey = "License.ProTrial";

    /// <summary>
    /// The file of the data folder with this installation's ID and the trials started here. It is not part of a backup, so a
    /// backup restored on another computer does not carry a running trial there.
    /// </summary>
    public const string InstallFileName = "install.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServerSettingsStore _settings;
    private readonly Func<string> _serverId;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _keys;
    private readonly string? _installFile;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _state = new();
    private string? _text;
    private TrialRecord? _trial;
    private InstallFile _install = new();
    private ProLicenseInfo _current = ProLicenseInfo.None;

    /// <param name="serverId">This server's ID (the one plugin licenses name, <c>PluginLicenses.ServerId</c>).</param>
    /// <param name="publicKeys">The signing keys (base64 SubjectPublicKeyInfo); null = <see cref="ProLicenseKeys"/> (tests pass their own).</param>
    /// <param name="dataDirectory">The data folder that holds <see cref="InstallFileName"/>; null = none (tests): the trial is kept in the database only.</param>
    public ProLicense(ServerSettingsStore settings, Func<string> serverId, TimeProvider? time = null, IReadOnlyList<string>? publicKeys = null, ILogger<ProLicense>? logger = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serverId);
        _settings = settings;
        _serverId = serverId;
        _time = time ?? TimeProvider.System;
        _keys = publicKeys ?? [.. ProLicenseKeys.All.Select(k => k.PublicKey)];
        _installFile = dataDirectory is null ? null : Path.Combine(dataDirectory, InstallFileName);
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public ProLicenseInfo Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>This server's ID.</summary>
    public string ServerId => _serverId();

    /// <summary>A license key is imported (Remove license removes it; the trial stays recorded).</summary>
    public bool HasKey
    {
        get
        {
            lock (_state)
            {
                return _text is not null;
            }
        }
    }

    /// <summary>Reads the stored license and the trial and checks them (server start, after the server ID exists).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var text = await _settings.GetAsync<string>(SettingKey, ct).ConfigureAwait(false);
        var trial = ReadTrial(await _settings.GetJsonAsync(TrialSettingKey, ct).ConfigureAwait(false));
        var install = await ReadInstallAsync(ct).ConfigureAwait(false);
        lock (_state)
        {
            _trial = trial;
            _install = install;
        }

        Update(text, setText: true);
    }

    /// <summary>
    /// Checks and stores a license key; it applies at once. Throws <see cref="ArgumentException"/> (the reason as message)
    /// for a key that is not valid, for another server or expired; nothing is stored then.
    /// </summary>
    public async Task<ProLicenseInfo> ImportAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var info = Check(text);
        if (!info.IsActive)
        {
            throw new ArgumentException(info.Reason);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settings.SetAsync(SettingKey, text.Trim(), CancellationToken.None).ConfigureAwait(false);
            info = Update(text.Trim(), setText: true); // inside the write lock: a Remove that follows cannot be overtaken
        }
        finally
        {
            _write.Release();
        }

        LogImported(_logger, info.Customer ?? "", info.Plan ?? "", info.IsTrial);
        return info;
    }

    /// <summary>Removes the license key; PRO is no longer active unless the trial still runs.</summary>
    public async Task<ProLicenseInfo> RemoveAsync(CancellationToken ct)
    {
        ProLicenseInfo info;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settings.ResetAsync(SettingKey, CancellationToken.None).ConfigureAwait(false);
            info = Update(null, setText: true);
        }
        finally
        {
            _write.Release();
        }

        LogRemoved(_logger);
        return info;
    }

    /// <summary>
    /// Starts the 30-day trial (<see cref="ProLicenseTexts.TrialDays"/> days including today), once per server: the first day
    /// goes into the database and into the install file. Throws <see cref="InvalidOperationException"/> with
    /// <see cref="ProLicenseTexts.TrialUsed"/> when a trial ran on this server before (also on another computer, or
    /// recorded only in the install file) and with <see cref="ProLicenseTexts.AlreadyActive"/> while a license is active.
    /// </summary>
    public async Task<ProLicenseInfo> StartTrialAsync(CancellationToken ct)
    {
        ProLicenseInfo info;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var server = ServerId;
            InstallFile install;
            lock (_state)
            {
                if (_trial is not null || _install.ProTrials.ContainsKey(server))
                {
                    throw new InvalidOperationException(ProLicenseTexts.TrialUsed);
                }

                if (_current.IsActive)
                {
                    throw new InvalidOperationException(ProLicenseTexts.AlreadyActive);
                }

                install = _install;
            }

            var today = Today();
            var installId = string.IsNullOrEmpty(install.Id) ? Guid.NewGuid().ToString("D") : install.Id;
            var nextInstall = new InstallFile
            {
                Id = installId,
                ProTrials = new Dictionary<string, DateOnly>(install.ProTrials, StringComparer.OrdinalIgnoreCase) { [server] = today },
            };
            var record = new TrialRecord(server, _installFile is null ? "" : installId, today);

            // The install file first: a database write that fails after it still leaves the trial used on this computer.
            await WriteInstallAsync(nextInstall, CancellationToken.None).ConfigureAwait(false);
            await _settings.SetJsonAsync(TrialSettingKey, JsonSerializer.Serialize(record, Json), CancellationToken.None).ConfigureAwait(false);
            lock (_state)
            {
                _install = nextInstall;
                _trial = record;
            }

            info = Update(null, setText: false);
        }
        finally
        {
            _write.Release();
        }

        LogTrialStarted(_logger, info.Expires is { } ends ? LicenseFile.Day(ends) : "");
        return info;
    }

    /// <summary>Checks the stored license and the trial again (one may have run out) and raises <see cref="Changed"/> when it changed.</summary>
    public ProLicenseInfo Refresh() => Update(null, setText: false);

    /// <summary>What <paramref name="text"/> says for this server today, without storing it.</summary>
    public ProLicenseInfo Check(string text) => ProLicenseFile.Check(text, _keys, ServerId, Today());

    public void Dispose() => _write.Dispose();

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>
    /// Sets the stored text (<paramref name="setText"/>) or checks the current one again, and the state from it and the
    /// trial, under one lock: an hourly check never overwrites an import, a removal or a trial start that happened meanwhile.
    /// </summary>
    private ProLicenseInfo Update(string? text, bool setText)
    {
        ProLicenseInfo info;
        bool changed;
        lock (_state)
        {
            if (setText)
            {
                _text = text;
            }

            info = Evaluate();
            changed = !Equals(Interlocked.Exchange(ref _current, info), info);
        }

        if (changed)
        {
            if (info.Status is ProLicenseStatus.Expired or ProLicenseStatus.WrongServer or ProLicenseStatus.Invalid)
            {
                LogNotActive(_logger, info.Reason);
            }

            Raise();
        }

        return info;
    }

    /// <summary>An active key, else a running trial, else the key's problem, else the ended trial, else no license.</summary>
    private ProLicenseInfo Evaluate()
    {
        var key = _text is null ? null : Check(_text);
        if (key is { IsActive: true })
        {
            return key;
        }

        var server = ServerId;
        var trial = TrialOf(server, Today());
        if (trial is { IsActive: true })
        {
            return trial;
        }

        var available = trial is null;
        return (key ?? trial ?? ProLicenseInfo.None) with { TrialAvailable = available };
    }

    /// <summary>The trial of this server: null = never started; started on this computer (either record) or elsewhere.</summary>
    private ProLicenseInfo? TrialOf(string server, DateOnly today)
    {
        DateOnly? started = _install.ProTrials.TryGetValue(server, out var local) ? local : null;
        var record = _trial is { } t && string.Equals(t.Server, server, StringComparison.OrdinalIgnoreCase) ? t : null;
        if (record is not null && (record.Install.Length == 0 || string.Equals(record.Install, _install.Id, StringComparison.OrdinalIgnoreCase)))
        {
            started = started is { } day && day < record.Started ? day : record.Started; // the earlier day wins
        }

        if (started is { } first)
        {
            var ends = first.AddDays(ProLicenseTexts.TrialDays - 1);
            return ends >= today
                ? new ProLicenseInfo(ProLicenseStatus.Active, ProLicenseTexts.Active(null, ends, trial: true), Expires: ends, IsTrial: true)
                : new ProLicenseInfo(ProLicenseStatus.Expired, ProLicenseTexts.TrialEndedOn(ends), Expires: ends, IsTrial: true);
        }

        // Recorded in the database by another installation (a backup restored here) or for another server ID: used, not running.
        return _trial is not null ? new ProLicenseInfo(ProLicenseStatus.Expired, ProLicenseTexts.TrialElsewhere, IsTrial: true) : null;
    }

    private static TrialRecord? ReadTrial(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrialRecord>(json, Json) is { Server: not null, Install: not null } record ? record : new TrialRecord("", "", DateOnly.MinValue);
        }
        catch (JsonException)
        {
            return new TrialRecord("", "", DateOnly.MinValue); // unreadable: counts as used, never as a fresh trial
        }
    }

    private async Task<InstallFile> ReadInstallAsync(CancellationToken ct)
    {
        if (_installFile is null || !File.Exists(_installFile))
        {
            return new InstallFile();
        }

        try
        {
            await using var stream = File.OpenRead(_installFile);
            var file = await JsonSerializer.DeserializeAsync<InstallFile>(stream, Json, ct).ConfigureAwait(false) ?? new InstallFile();
            return new InstallFile { Id = file.Id ?? "", ProTrials = new Dictionary<string, DateOnly>(file.ProTrials ?? [], StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogInstallFileUnreadable(_logger, ex);
            return new InstallFile();
        }
    }

    private async Task WriteInstallAsync(InstallFile install, CancellationToken ct)
    {
        if (_installFile is null)
        {
            return;
        }

        var temp = _installFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(install, Json), ct).ConfigureAwait(false);
        File.Move(temp, _installFile, overwrite: true);
    }

    /// <summary>Every handler on its own: a plugin's failing handler never stops the others, the hourly check or an import.</summary>
    private void Raise()
    {
        // The background boundary: an import or removal by an administrator starts and stops PRO plugins; neither they nor
        // the work the handlers start see that user.
        using var detached = Auth.CallerContext.Suppress();
        foreach (var handler in Changed?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // Plugin code is untrusted; the failure is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerFailed(_logger, ex);
            }
        }
    }

    /// <summary>The trial in the database: for which server, on which installation, the first day.</summary>
    internal sealed record TrialRecord(string Server, string Install, DateOnly Started);

    /// <summary>The install file: this installation's ID and the first day of the trial per server ID started here.</summary>
    internal sealed class InstallFile
    {
        public string Id { get; init; } = "";

        public Dictionary<string, DateOnly> ProTrials { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM PRO license imported: {Customer}, {Plan}, trial {Trial}")]
    private static partial void LogImported(ILogger logger, string customer, string plan, bool trial);

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM PRO license removed")]
    private static partial void LogRemoved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM PRO trial started, last day {Ends}")]
    private static partial void LogTrialStarted(ILogger logger, string ends);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The install file could not be read; a trial recorded only there is not known")]
    private static partial void LogInstallFileUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A handler of the OADM PRO license change failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OADM PRO license is not active: {Reason}")]
    private static partial void LogNotActive(ILogger logger, string reason);
}
