using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Backup;

/// <summary>Automatic backups (setting <c>Backup.Schedule</c>; the password encrypted with the server key).</summary>
public sealed record BackupScheduleSetting
{
    public const int DefaultKeep = 7;

    public bool Enabled { get; init; }

    /// <summary>"02:00", server time.</summary>
    public string TimeOfDay { get; init; } = "02:00";

    public int Keep { get; init; } = DefaultKeep;

    /// <summary>The password, encrypted (<see cref="ISecretProtector"/>, purpose <see cref="BackupManager.PasswordPurpose"/>).</summary>
    public string? ProtectedPassword { get; init; }

    public static string? TimeProblem(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Enter a time like 02:00.";
}

/// <summary>The last automatic backup (setting <c>Backup.LastAutomatic</c>).</summary>
public sealed record LastAutomaticBackup(DateTimeOffset TimeUtc, string? Error);

/// <summary>The report of the last restore (setting <c>Backup.LastRestore</c>), shown on the Settings page.</summary>
public sealed record RestoreReportSetting
{
    public string Source { get; init; } = "";
    public DateTimeOffset AppliedUtc { get; init; }
    public string FromVersion { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
}

/// <summary>A backup kept in the server's backups folder.</summary>
public sealed record ServerBackupFile(string Name, long Size, DateTimeOffset CreatedUtc, string Kind);

/// <summary>A backup's details for the confirmation before a restore.</summary>
public sealed record BackupInspection(bool WrongPassword, string? Problem, BackupHeader? Header, IReadOnlyList<string> Lines);

/// <summary>A restore request that cannot be done; the message is for the user. Nothing was changed.</summary>
public sealed class BackupRefusedException(string message) : Exception(message);

/// <summary>Restarts the server after a staged restore (tests replace it).</summary>
public interface IServerRestart
{
    void Request();
}

/// <summary>
/// The backup and restore of this server (Settings page, BackupService): backups made on request (streamed to the client)
/// and automatically (kept in <c>&lt;datafolder&gt;/backups</c>), the check of a backup before a restore, the restore (a
/// safety backup of the current state first, running tasks cancelled, the server restarts and applies it) and the report
/// after it.
/// </summary>
public sealed partial class BackupManager(
    OadmPaths paths,
    BackupArchive archive,
    ServerSettingsStore settings,
    ISecretProtector secrets,
    PluginRegistry plugins,
    TaskEngine tasks,
    IServerRestart restart,
    IDbContextFactory<OadmDbContext> dbFactory,
    AuditLog audit,
    TimeProvider time,
    ILogger<BackupManager> logger) : IDisposable
{
    public const string ScheduleKey = "Backup.Schedule";
    public const string LastAutomaticKey = "Backup.LastAutomatic";
    public const string LastRestoreKey = "Backup.LastRestore";
    public const string PasswordPurpose = "oadm:backup-schedule";
    public const string KindAutomatic = "automatic";
    public const string KindBeforeRestore = "before-restore";
    public const string KindManual = "manual";
    private const int KeepBeforeRestore = 5;

    [GeneratedRegex(@"^oadm-backup-\d{8}-\d{6}-(automatic|before-restore)\.oadmbackup\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private readonly SemaphoreSlim _busy = new(1, 1);

    public string Folder => archive.BackupsDirectory;

    public void Dispose() => _busy.Dispose();

    private volatile bool _restarting;

    /// <summary>One backup or restore at a time; none after a restore asked for the restart.</summary>
    private async Task EnterAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        if (_restarting)
        {
            _busy.Release();
            throw new BackupRefusedException("The server is restarting to restore a backup. Try again in a moment.");
        }
    }

    /// <summary>Startup: backups that were being written when the server stopped (*.part) are removed.</summary>
    public void RemovePartialFiles()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var part in Directory.GetFiles(Folder, "*" + BackupFile.Extension + ".part"))
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed at the next start.
            }
        }
    }

    public static string ServerVersion { get; } =
        (typeof(BackupManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public async Task<BackupScheduleSetting> GetScheduleAsync(CancellationToken ct) =>
        await settings.GetAsync<BackupScheduleSetting>(ScheduleKey, ct).ConfigureAwait(false) ?? new BackupScheduleSetting();

    /// <summary>Saves the automatic backups; an empty password keeps the stored one. Throws <see cref="ArgumentException"/>.</summary>
    public async Task<BackupScheduleSetting> SetScheduleAsync(bool enabled, string timeOfDay, int keep, string? password, CancellationToken ct)
    {
        if (BackupScheduleSetting.TimeProblem(timeOfDay) is { } timeProblem)
        {
            throw new ArgumentException(timeProblem, nameof(timeOfDay));
        }

        if (keep is < 1 or > 365)
        {
            throw new ArgumentException("Keep 1 to 365 backups.", nameof(keep));
        }

        var current = await GetScheduleAsync(ct).ConfigureAwait(false);
        var protectedPassword = current.ProtectedPassword;
        if (!string.IsNullOrEmpty(password))
        {
            if (BackupFile.PasswordProblem(password) is { } problem)
            {
                throw new ArgumentException(problem, nameof(password));
            }

            protectedPassword = secrets.Protect(password, PasswordPurpose);
        }

        if (enabled && protectedPassword is null)
        {
            throw new ArgumentException("Enter a password for the automatic backups.", nameof(password));
        }

        var saved = new BackupScheduleSetting { Enabled = enabled, TimeOfDay = timeOfDay, Keep = keep, ProtectedPassword = protectedPassword };
        await settings.SetAsync(ScheduleKey, saved, ct).ConfigureAwait(false);
        await PruneAsync(KindAutomatic, keep).ConfigureAwait(false);
        return saved;
    }

    public async Task<LastAutomaticBackup?> GetLastAutomaticAsync(CancellationToken ct) =>
        await settings.GetAsync<LastAutomaticBackup>(LastAutomaticKey, ct).ConfigureAwait(false);

    public async Task<RestoreReportSetting?> GetLastRestoreAsync(CancellationToken ct) =>
        await settings.GetAsync<RestoreReportSetting>(LastRestoreKey, ct).ConfigureAwait(false);

    public int ActiveTasks => tasks.ActiveCount;

    /// <summary>The backups kept on the server, newest first.</summary>
    public IReadOnlyList<ServerBackupFile> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return Directory.GetFiles(Folder, "*" + BackupFile.Extension)
            .Select(path => new FileInfo(path))
            .Where(f => NamePattern().IsMatch(f.Name))
            .Select(f => new ServerBackupFile(f.Name, f.Length, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero), KindOf(f.Name)))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string KindOf(string name) => name.Contains("-" + KindBeforeRestore, StringComparison.Ordinal) ? KindBeforeRestore : KindAutomatic;

    /// <summary>The path of a backup kept on the server; null for an unknown name or anything that is not one.</summary>
    public string? PathOf(string? name)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            return null;
        }

        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>"oadm-backup-site-a-2026-10-13-1402.oadmbackup": the name the client saves a backup made on request under.</summary>
    public string DownloadName(string serverName) =>
        $"oadm-backup-{SafeName(serverName)}-{time.GetLocalNow().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}{BackupFile.Extension}";

    private static string SafeName(string name)
    {
        var safe = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return safe.Length == 0 ? "server" : safe.Length > 40 ? safe[..40] : safe;
    }

    private async Task<BackupHeader> HeaderAsync(string kind, CancellationToken ct) => new()
    {
        Kind = kind,
        OadmVersion = ServerVersion,
        ServerName = (await settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName,
    };

    private IReadOnlyList<BackupPlugin> Plugins() =>
        [.. plugins.Packages.Select(p => new BackupPlugin(p.Id, p.Origin.Version)).OrderBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>Writes a backup to <paramref name="output"/> ("Back up now"). One backup or restore at a time.</summary>
    public async Task<BackupContents> CreateAsync(Stream output, string password, CancellationToken ct)
    {
        if (BackupFile.PasswordProblem(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return await archive.CreateAsync(output, password, await HeaderAsync(KindManual, ct).ConfigureAwait(false), Plugins(), ct).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Makes a backup in the server's folder (written as .part, then renamed). Returns its name.</summary>
    private async Task<string> CreateFileAsync(string kind, string password, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var name = $"oadm-backup-{time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{kind}{BackupFile.Extension}";
        var path = Path.Combine(Folder, name);
        var part = path + ".part";
        try
        {
            var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await archive.CreateAsync(file, password, await HeaderAsync(kind, ct).ConfigureAwait(false), Plugins(), ct).ConfigureAwait(false);
            }

            File.Move(part, path, overwrite: true);
            return name;
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    /// <summary>The automatic backup (schedule): made, then the oldest beyond Keep removed; the outcome stored.</summary>
    public async Task RunAutomaticAsync(CancellationToken ct)
    {
        var schedule = await GetScheduleAsync(ct).ConfigureAwait(false);
        string? error = null;
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var password = secrets.Unprotect(schedule.ProtectedPassword ?? throw new InvalidOperationException("The automatic backups have no password."), PasswordPurpose);
            var name = await CreateFileAsync(KindAutomatic, password, ct).ConfigureAwait(false);
            await PruneAsync(KindAutomatic, schedule.Keep).ConfigureAwait(false);
            LogAutomatic(name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            LogAutomaticFailed(ex);
        }
        finally
        {
            _busy.Release();
        }

        await settings.SetAsync(LastAutomaticKey, new LastAutomaticBackup(time.GetUtcNow(), error), ct).ConfigureAwait(false);
    }

    private Task PruneAsync(string kind, int keep)
    {
        foreach (var old in List().Where(b => b.Kind == kind).Skip(keep))
        {
            File.Delete(Path.Combine(Folder, old.Name));
        }

        return Task.CompletedTask;
    }

    /// <summary>Checks a backup with its password; nothing changes. A wrong password or a problem is in the result.</summary>
    public async Task<BackupInspection> InspectAsync(Func<CancellationToken, Task<Stream>> open, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        var stream = await open(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            BackupHeader header;
            try
            {
                header = BackupFile.ReadHeader(stream);
            }
            catch (BackupFileException ex)
            {
                return new BackupInspection(false, ex.Message, null, []);
            }

            if (VersionProblem(header) is { } problem)
            {
                return new BackupInspection(false, problem, header, []);
            }
        }

        stream = await open(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var contents = await archive.InspectAsync(stream, password, ct).ConfigureAwait(false);
                return new BackupInspection(false, null, contents.Header, Lines(contents.Manifest.Counts, contents.Manifest.PluginData.Count, restored: false));
            }
            catch (BackupFileException ex)
            {
                return new BackupInspection(ex.IsWrongPassword, ex.IsWrongPassword ? null : ex.Message, null, []);
            }
        }
    }

    private async Task<string?> VersionProblemAsync(Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        var stream = await open(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return VersionProblem(BackupFile.ReadHeader(stream));
            }
            catch (BackupFileException ex)
            {
                return ex.Message;
            }
        }
    }

    private string? VersionProblem(BackupHeader header)
    {
        using var db = dbFactory.CreateDbContext();
        return BackupArchive.VersionProblem(header, [.. db.Database.GetMigrations()], ServerVersion);
    }

    /// <summary>
    /// Restore: checks the backup and stages it (a wrong password or a damaged file changes nothing), makes a backup of the
    /// current state with the same password, cancels the running tasks and restarts the server, which applies it. Throws
    /// <see cref="BackupRefusedException"/> with the reason. Returns the name of the safety backup.
    /// </summary>
    public async Task<string> RestoreAsync(Func<CancellationToken, Task<Stream>> open, string source, string password, string requestedBy, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (await VersionProblemAsync(open, ct).ConfigureAwait(false) is { } problem)
        {
            throw new BackupRefusedException(problem + " Nothing was changed.");
        }

        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            PendingRestoreInfo staged;
            var stream = await open(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    staged = await archive.StageAsync(stream, password, source, requestedBy, clientAddress, ct).ConfigureAwait(false);
                }
                catch (BackupFileException ex)
                {
                    throw new BackupRefusedException(ex.Message + " Nothing was changed.");
                }
            }

            // Staged: from here on it either reaches the restart or is dropped (a cancelled call never leaves a restore
            // that a later restart would apply without a safety backup). The client going away does not stop it.
            string safety;
            try
            {
                safety = await CreateFileAsync(KindBeforeRestore, password, CancellationToken.None).ConfigureAwait(false);
                await PruneAsync(KindBeforeRestore, KeepBeforeRestore).ConfigureAwait(false);
                await tasks.CancelAllAsync(CancellationToken.None).ConfigureAwait(false);
                await audit.WriteAsync(AuditActions.RestoreStarted, staged.Source, $"From OADM {staged.Header.OadmVersion}, {staged.Header.CreatedUtc:yyyy-MM-dd HH:mm} UTC; current state saved as {safety}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                archive.CancelPending();
                throw new BackupRefusedException("The backup of the current state failed: " + ex.Message + " Nothing was changed.");
            }

            LogRestoreStaged(staged.Source, safety);
            _restarting = true;
            restart.Request();
            return safety;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Startup, after the database and the plugins are ready: records the restore the start applied (report, audit) and
    /// ends every login session (everyone logs in with the restored users).
    /// </summary>
    public async Task RecordRestoreAsync(CancellationToken ct)
    {
        if (archive.ReadOutcome() is not { } outcome)
        {
            return;
        }

        try
        {
            await RecordAsync(outcome, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The restore itself is done; a report that cannot be written never keeps the server from starting.
            LogReportFailed(ex);
        }

        archive.ForgetOutcome();
    }

    private async Task RecordAsync(RestoreOutcome outcome, CancellationToken ct)
    {
        var restore = outcome.Restore;
        var lines = new List<string>();
        if (outcome.Error is null)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                await db.AuthTokens.ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            var counts = await BackupArchive.CountAsync(paths.DatabasePath, ct).ConfigureAwait(false);
            lines.AddRange(Lines(counts, restore.Manifest.PluginData.Count, restored: true));
            var installed = plugins.Packages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in restore.Manifest.Plugins.Where(p => !installed.Contains(p.Id)))
            {
                lines.Add($"Plugin {missing.Id}: not installed on this server; its settings were kept.");
            }
        }

        var report = new RestoreReportSetting
        {
            Source = restore.Source,
            AppliedUtc = outcome.AppliedUtc,
            FromVersion = restore.Header.OadmVersion,
            RequestedBy = restore.RequestedBy,
            Error = outcome.Error,
            Lines = lines,
        };
        await settings.SetAsync(LastRestoreKey, report, ct).ConfigureAwait(false);
        await audit.WriteAsync(
            restore.RequestedBy.Split('@')[0],
            restore.ClientAddress,
            AuditActions.RestoreApplied,
            restore.Source,
            outcome.Error is null ? string.Join("; ", lines) : "Failed, the server kept its data: " + outcome.Error,
            ct).ConfigureAwait(false);
    }

    /// <summary>"Devices: 4,812", ... (restored: "Devices: 4,812 restored").</summary>
    public static IReadOnlyList<string> Lines(BackupCounts counts, int pluginFolders, bool restored)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var suffix = restored ? " restored" : string.Empty;
        string Line(string area, int count) => string.Create(CultureInfo.InvariantCulture, $"{area}: {count:N0}{suffix}");
        return
        [
            Line("Devices", counts.Devices),
            Line("Device logins", counts.DeviceLogins),
            Line("Credential list", counts.CredentialList),
            Line("Users", counts.Users),
            Line("Roles", counts.Roles),
            Line("Tags", counts.Tags),
            Line("Event rules", counts.EventRules),
            Line("Tasks in the history", counts.Tasks),
            Line("Plugin data folders", pluginFolders),
        ];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The report of the restore could not be written")]
    private partial void LogReportFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic backup {Name} made")]
    private partial void LogAutomatic(string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "The automatic backup failed")]
    private partial void LogAutomaticFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore of {Source} staged (current state saved as {Safety}); the server restarts to apply it")]
    private partial void LogRestoreStaged(string source, string safety);
}
