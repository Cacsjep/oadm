using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;

namespace Oadm.Core.Licensing;

/// <summary>
/// The server's OADM PRO license (docs/spec/licensing.md): a license key in the plugin license format
/// (<see cref="ProLicenseFile"/>) signed with one of <see cref="ProLicenseKeys"/>, for this
/// server's ID. The imported key is kept in the setting <see cref="SettingKey"/> and checked again at start and by
/// <see cref="Refresh"/> (hourly, so an expiry reaches the plugins the same day). OADM itself never depends on it.
/// </summary>
public sealed partial class ProLicense : IProLicense, IDisposable
{
    /// <summary>The setting that holds the imported license key.</summary>
    public const string SettingKey = "License.Pro";

    private readonly ServerSettingsStore _settings;
    private readonly Func<string> _serverId;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _keys;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _state = new();
    private string? _text;
    private ProLicenseInfo _current = ProLicenseInfo.None;

    /// <param name="serverId">This server's ID (the one plugin licenses name, <c>PluginLicenses.ServerId</c>).</param>
    /// <param name="publicKeys">The signing keys (base64 SubjectPublicKeyInfo); null = <see cref="ProLicenseKeys"/> (tests pass their own).</param>
    public ProLicense(ServerSettingsStore settings, Func<string> serverId, TimeProvider? time = null, IReadOnlyList<string>? publicKeys = null, ILogger<ProLicense>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serverId);
        _settings = settings;
        _serverId = serverId;
        _time = time ?? TimeProvider.System;
        _keys = publicKeys ?? [.. ProLicenseKeys.All.Select(k => k.PublicKey)];
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public ProLicenseInfo Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>This server's ID.</summary>
    public string ServerId => _serverId();

    /// <summary>Reads the stored license and checks it (server start, after the server ID exists).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var text = await _settings.GetAsync<string>(SettingKey, ct).ConfigureAwait(false);
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

    /// <summary>Removes the license; PRO is no longer active.</summary>
    public async Task<ProLicenseInfo> RemoveAsync(CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settings.ResetAsync(SettingKey, CancellationToken.None).ConfigureAwait(false);
            Update(null, setText: true);
        }
        finally
        {
            _write.Release();
        }

        LogRemoved(_logger);
        return ProLicenseInfo.None;
    }

    /// <summary>Checks the stored license again (it may have run out) and raises <see cref="Changed"/> when it changed.</summary>
    public ProLicenseInfo Refresh() => Update(null, setText: false);

    /// <summary>What <paramref name="text"/> says for this server today, without storing it.</summary>
    public ProLicenseInfo Check(string text) =>
        ProLicenseFile.Check(text, _keys, ServerId, DateOnly.FromDateTime(_time.GetLocalNow().DateTime));

    public void Dispose() => _write.Dispose();

    /// <summary>
    /// Sets the stored text (<paramref name="setText"/>) or checks the current one again, and the state from it, under one
    /// lock: an hourly check never overwrites an import or a removal that happened meanwhile.
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

            info = _text is null ? ProLicenseInfo.None : Check(_text);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM PRO license imported: {Customer}, {Plan}, trial {Trial}")]
    private static partial void LogImported(ILogger logger, string customer, string plan, bool trial);

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM PRO license removed")]
    private static partial void LogRemoved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A handler of the OADM PRO license change failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OADM PRO license is not active: {Reason}")]
    private static partial void LogNotActive(ILogger logger, string reason);
}
