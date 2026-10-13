using System.IO.Compression;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;

namespace Oadm.Core.Backup;

/// <summary>How many of each thing a backup holds: the restore report compares them.</summary>
public sealed record BackupCounts
{
    public int Devices { get; init; }
    public int DeviceLogins { get; init; }
    public int CredentialList { get; init; }
    public int Users { get; init; }
    public int Roles { get; init; }
    public int Tags { get; init; }
    public int EventRules { get; init; }
    public int Tasks { get; init; }
}

/// <summary>A plugin package of the server that made the backup.</summary>
public sealed record BackupPlugin(string Id, string Version);

/// <summary><c>manifest.json</c> inside the encrypted body.</summary>
public sealed record BackupManifest
{
    public BackupCounts Counts { get; init; } = new();

    public IReadOnlyList<BackupPlugin> Plugins { get; init; } = [];

    /// <summary>Folders under plugin-data (one per plugin id that keeps working files).</summary>
    public IReadOnlyList<string> PluginData { get; init; } = [];
}

/// <summary>What a backup holds, read with the password (the whole file is checked on the way).</summary>
public sealed record BackupContents(BackupHeader Header, BackupManifest Manifest);

/// <summary>Written next to the staged files; the next start applies them.</summary>
public sealed record PendingRestoreInfo
{
    public BackupHeader Header { get; init; } = new();
    public BackupManifest Manifest { get; init; } = new();

    /// <summary>"oadm-backup-....oadmbackup" or the uploaded file's name.</summary>
    public string Source { get; init; } = "";

    /// <summary>The administrator who asked ("admin@PC-1").</summary>
    public string RequestedBy { get; init; } = "";

    public string ClientAddress { get; init; } = "";

    public DateTimeOffset StagedUtc { get; init; }
}

/// <summary>The outcome of an applied restore (until the server has recorded it).</summary>
public sealed record RestoreOutcome
{
    public PendingRestoreInfo Restore { get; init; } = new();

    public DateTimeOffset AppliedUtc { get; init; }

    /// <summary>Null when the files were swapped; otherwise why the server kept its data.</summary>
    public string? Error { get; init; }

    /// <summary>Where the replaced data was kept ("restore/previous-20261013-140211").</summary>
    public string? PreviousFolder { get; init; }
}

/// <summary>
/// The backup content and the restore swap. A backup holds the database (a consistent copy by <c>VACUUM INTO</c>, without the
/// login sessions), <c>master.key</c> (every stored password and plugin secret is encrypted with it; inside the encrypted
/// body only) and the plugins' working folders. A restore is staged first and applied by the next start, before the
/// database is opened (<see cref="ApplyPending"/>); the replaced files are kept in <c>restore/previous-*</c>.
/// </summary>
public sealed partial class BackupArchive(OadmPaths paths, ILogger<BackupArchive>? logger = null, TimeProvider? time = null)
{
    public const string DatabaseEntry = "oadm.db";
    public const string KeyEntry = "master.key";
    public const string ManifestEntry = "manifest.json";
    public const string PluginDataPrefix = "plugin-data/";
    private const string PendingFile = "pending.json";
    private const string ResultFile = "result.json";
    private const int KeepPrevious = 3;

    private readonly ILogger _logger = logger ?? NullLogger<BackupArchive>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>PBKDF2 iterations of new backups (tests lower it).</summary>
    public int Iterations { get; init; } = BackupFile.DefaultIterations;

    public string BackupsDirectory => Path.Combine(paths.DataDirectory, "backups");

    public string RestoreDirectory => Path.Combine(paths.DataDirectory, "restore");

    private string TempDirectory => Path.Combine(BackupsDirectory, "tmp");

    /// <summary>The last migration of the database at <paramref name="databasePath"/> (empty for a new database).</summary>
    public static string LastMigration(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        try
        {
            return command.ExecuteScalar() as string ?? string.Empty;
        }
        catch (SqliteException)
        {
            return string.Empty;
        }
    }

    /// <summary>A consistent copy of the live database (readers and writers keep working meanwhile).</summary>
    public async Task CopyDatabaseAsync(string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Delete(target);
        var connection = new SqliteConnection(paths.ConnectionString + ";Pooling=False");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "VACUUM INTO $target";
                command.Parameters.AddWithValue("$target", target);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes a backup of this server to <paramref name="output"/>, encrypted with <paramref name="password"/>.
    /// <paramref name="header"/> brings the kind, OADM version and server name; the rest is filled in.
    /// </summary>
    public async Task<BackupContents> CreateAsync(Stream output, string password, BackupHeader header, IReadOnlyList<BackupPlugin> plugins, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(plugins);
        var work = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var copy = Path.Combine(work, DatabaseEntry);
            await CopyDatabaseAsync(copy, ct).ConfigureAwait(false);
            var counts = await PrepareCopyAsync(copy, ct).ConfigureAwait(false);
            var pluginData = Directory.Exists(paths.PluginDataDirectory)
                ? Directory.GetDirectories(paths.PluginDataDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
                : [];
            var manifest = new BackupManifest { Counts = counts, Plugins = plugins, PluginData = pluginData };
            var full = header with { CreatedUtc = _time.GetUtcNow(), Migration = LastMigration(copy) };

            var body = BackupFile.Create(output, full, password, Iterations);
            await using (body.ConfigureAwait(false))
            {
                using var zip = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true);
                await using (var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open())
                {
                    await JsonSerializer.SerializeAsync(entry, manifest, BackupFile.Json, ct).ConfigureAwait(false);
                }

                await AddFileAsync(zip, copy, DatabaseEntry, ct).ConfigureAwait(false);
                await AddFileAsync(zip, paths.MasterKeyPath, KeyEntry, ct).ConfigureAwait(false);
                foreach (var folder in pluginData)
                {
                    var root = Path.Combine(paths.PluginDataDirectory, folder);
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(paths.PluginDataDirectory, file).Replace('\\', '/');
                        FileStream source;
                        try
                        {
                            source = OpenShared(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // A plugin's working file in use (e.g. a report being written): left out, the rest is kept.
                            LogFileSkipped(ex, file);
                            continue;
                        }

                        // Opened: a failure while copying fails the backup (never a cut entry in a "good" backup).
                        await AddStreamAsync(zip, source, PluginDataPrefix + relative, ct).ConfigureAwait(false);
                    }
                }
            }

            return new BackupContents(full, manifest);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// Removes the login sessions from the copy (a restored server never resumes an old login) and counts what it holds.
    /// Plain SQL by table name: a database of an older OADM may not have every table yet (counted 0).
    /// </summary>
    private static async Task<BackupCounts> PrepareCopyAsync(string copy, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={copy};Pooling=False");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "DELETE FROM AuthTokens", ct).ConfigureAwait(false);
            return await CountAsync(connection, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How many devices, logins, users ... the database at <paramref name="path"/> holds (the restore report).</summary>
    public static async Task<BackupCounts> CountAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return await CountAsync(connection, ct).ConfigureAwait(false);
        }
    }

    private static async Task<BackupCounts> CountAsync(SqliteConnection connection, CancellationToken ct) => new()
    {
        Devices = await CountAsync(connection, "Devices", ct).ConfigureAwait(false),
        DeviceLogins = await CountAsync(connection, "DeviceCredentials", ct).ConfigureAwait(false),
        CredentialList = await CountAsync(connection, "CredentialListEntries", ct).ConfigureAwait(false),
        Users = await CountAsync(connection, "Users", ct).ConfigureAwait(false),
        Roles = await CountAsync(connection, "Roles", ct).ConfigureAwait(false),
        Tags = await CountAsync(connection, "TagDefinitions", ct).ConfigureAwait(false),
        EventRules = await CountAsync(connection, "EventRules", ct).ConfigureAwait(false),
        Tasks = await CountAsync(connection, "Tasks", ct).ConfigureAwait(false),
    };

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            try
            {
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                return 0; // the table came with a later OADM
            }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            try
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // No sessions table in a database of an older OADM.
            }
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);

    private static Task AddFileAsync(ZipArchive zip, string path, string name, CancellationToken ct) => AddStreamAsync(zip, OpenShared(path), name, ct);

    private static async Task AddStreamAsync(ZipArchive zip, FileStream source, string name, CancellationToken ct)
    {
        await using (source.ConfigureAwait(false))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            await using (entry.ConfigureAwait(false))
            {
                await source.CopyToAsync(entry, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Checks a backup with its password: every chunk is decrypted and the ZIP read (nothing is kept). Throws
    /// <see cref="BackupFileException"/> for a wrong password or a damaged file.
    /// </summary>
    public async Task<BackupContents> InspectAsync(Stream input, string password, CancellationToken ct)
    {
        var work = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var (header, zipPath) = await DecryptToFileAsync(input, password, work, ct).ConfigureAwait(false);
            return new BackupContents(header, ReadManifest(zipPath));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Reads only the header (no password): the version check happens before the password is asked for.</summary>
    public static BackupHeader ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        return BackupFile.ReadHeader(stream);
    }

    /// <summary>
    /// Why this server cannot restore <paramref name="header"/>; null when it can. <paramref name="known"/> are the
    /// migrations of this build (in order): a backup's migration must be one of them. Older backups are upgraded by the same
    /// migrations as an installed update.
    /// </summary>
    public static string? VersionProblem(BackupHeader header, IReadOnlyList<string> known, string serverVersion)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(known);
        if (string.IsNullOrEmpty(header.Migration) || known.Contains(header.Migration, StringComparer.Ordinal))
        {
            return null;
        }

        return known.Count == 0 || string.CompareOrdinal(header.Migration, known[^1]) > 0
            ? $"This backup is from OADM {header.OadmVersion}, newer than this server ({serverVersion}). Update OADM to {header.OadmVersion} or later first."
            : $"This backup is from OADM {header.OadmVersion} and cannot be read by this server ({serverVersion}).";
    }

    /// <summary>
    /// Decrypts and unpacks a backup into <c>restore/staged</c> and records it in <c>restore/pending.json</c>: the next start
    /// swaps the files (<see cref="ApplyPending"/>). Only the known entries are unpacked; nothing outside the folder.
    /// </summary>
    public async Task<PendingRestoreInfo> StageAsync(Stream input, string password, string source, string requestedBy, string clientAddress, CancellationToken ct)
    {
        Directory.CreateDirectory(RestoreDirectory);
        var staged = Path.Combine(RestoreDirectory, "staged");
        TryDelete(staged);
        File.Delete(Path.Combine(RestoreDirectory, PendingFile));
        var work = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var (header, zipPath) = await DecryptToFileAsync(input, password, work, ct).ConfigureAwait(false);
            var manifest = ReadManifest(zipPath);
            Directory.CreateDirectory(staged);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (TargetOf(staged, entry.FullName) is not { } target)
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var key = Path.Combine(staged, KeyEntry);
            if (!File.Exists(Path.Combine(staged, DatabaseEntry)) || !File.Exists(key) || new FileInfo(key).Length != Security.CredentialProtector.KeySize)
            {
                throw new BackupFileException("The backup is incomplete (database or key missing).");
            }

            var info = new PendingRestoreInfo
            {
                Header = header, Manifest = manifest, Source = source, RequestedBy = requestedBy, ClientAddress = clientAddress, StagedUtc = _time.GetUtcNow(),
            };
            await File.WriteAllTextAsync(Path.Combine(RestoreDirectory, PendingFile), JsonSerializer.Serialize(info, BackupFile.Json), ct).ConfigureAwait(false);
            return info;
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>The staged path of a ZIP entry; null for anything that is not a backup part (or would leave the folder).</summary>
    private static string? TargetOf(string staged, string name)
    {
        if (name is DatabaseEntry or KeyEntry)
        {
            return Path.Combine(staged, name);
        }

        if (!name.StartsWith(PluginDataPrefix, StringComparison.Ordinal) || name.EndsWith('/') || name.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = name.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
        {
            return null;
        }

        var target = Path.GetFullPath(Path.Combine(staged, Path.Combine(parts)));
        var root = Path.GetFullPath(Path.Combine(staged, "plugin-data")) + Path.DirectorySeparatorChar;
        return target.StartsWith(root, StringComparison.Ordinal) ? target : null;
    }

    public bool HasPending => File.Exists(Path.Combine(RestoreDirectory, PendingFile));

    /// <summary>Drops a staged restore (its safety backup failed): the next start applies nothing.</summary>
    public void CancelPending()
    {
        File.Delete(Path.Combine(RestoreDirectory, PendingFile));
        TryDelete(Path.Combine(RestoreDirectory, "staged"));
    }

    /// <summary>
    /// Startup, before the database and the master key are opened: swaps a staged restore into place. The replaced files go to
    /// <c>restore/previous-&lt;time&gt;</c> (the newest three kept). <paramref name="keyChanged"/> gets the old and the new
    /// master key, for files outside the backup that are encrypted with it (the server's TLS key). A failure puts the old
    /// files back. Returns null when nothing was staged.
    /// </summary>
    public RestoreOutcome? ApplyPending(Action<byte[], byte[]>? keyChanged = null)
    {
        RecoverInterruptedSwap();
        var pendingPath = Path.Combine(RestoreDirectory, PendingFile);
        if (!File.Exists(pendingPath))
        {
            return null;
        }

        PendingRestoreInfo info;
        try
        {
            info = JsonSerializer.Deserialize<PendingRestoreInfo>(File.ReadAllText(pendingPath), BackupFile.Json) ?? new PendingRestoreInfo();
        }
        catch (JsonException)
        {
            info = new PendingRestoreInfo();
        }

        File.Delete(pendingPath); // never twice, whatever happens next
        SqliteConnection.ClearAllPools(); // no pooled connection may keep the old database open
        var staged = Path.Combine(RestoreDirectory, "staged");
        var previous = Path.Combine(RestoreDirectory, "previous-" + _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        var moved = new List<(string From, string To)>();

        // A process that dies during the moves leaves this marker: the next start puts the server's own files back.
        var swapping = Path.Combine(RestoreDirectory, SwapMarkerFile);
        File.WriteAllText(swapping, previous);
        RestoreOutcome outcome;
        try
        {
            var oldKey = File.Exists(paths.MasterKeyPath) ? File.ReadAllBytes(paths.MasterKeyPath) : null;
            var newKey = File.ReadAllBytes(Path.Combine(staged, KeyEntry));
            Directory.CreateDirectory(previous);
            foreach (var name in new[] { OadmPaths.DatabaseFileName, OadmPaths.DatabaseFileName + "-wal", OadmPaths.DatabaseFileName + "-shm", OadmPaths.MasterKeyFileName })
            {
                Move(Path.Combine(paths.DataDirectory, name), Path.Combine(previous, name), moved);
            }

            Move(paths.PluginDataDirectory, Path.Combine(previous, "plugin-data"), moved);
            Move(Path.Combine(staged, DatabaseEntry), paths.DatabasePath, moved);
            Move(Path.Combine(staged, KeyEntry), paths.MasterKeyPath, moved);
            Move(Path.Combine(staged, "plugin-data"), paths.PluginDataDirectory, moved);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(paths.MasterKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (oldKey is not null)
            {
                keyChanged?.Invoke(oldKey, newKey);
            }

            outcome = new RestoreOutcome { Restore = info, AppliedUtc = _time.GetUtcNow(), PreviousFolder = Path.GetRelativePath(paths.DataDirectory, previous) };
            LogApplied(info.Source, previous);
        }
#pragma warning disable CA1031 // Any failure keeps (or puts back) the server's own data; the outcome says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    Move(moved[i].To, moved[i].From, null);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the log names the folder with the server's previous files.
                    LogMoveBackFailed(back, moved[i].To);
                }
            }

            if (Directory.Exists(previous) && !Directory.EnumerateFileSystemEntries(previous).Any())
            {
                Directory.Delete(previous);
            }

            LogApplyFailed(ex, previous);
            outcome = new RestoreOutcome { Restore = info, AppliedUtc = _time.GetUtcNow(), Error = ex.Message };
        }

        File.Delete(swapping);
        TryDelete(staged);
        PrunePrevious();
        File.WriteAllText(Path.Combine(RestoreDirectory, ResultFile), JsonSerializer.Serialize(outcome, BackupFile.Json));
        return outcome;
    }

    /// <summary>The marker of a running swap: the folder that holds the server's own files meanwhile.</summary>
    public const string SwapMarkerFile = "swapping.txt";

    /// <summary>
    /// A swap the process did not finish (the marker is still there): when the database or the key is missing, the server's
    /// own files are moved back from the previous folder, so the start never comes up with an empty database and a new key.
    /// </summary>
    private void RecoverInterruptedSwap()
    {
        var marker = Path.Combine(RestoreDirectory, SwapMarkerFile);
        if (!File.Exists(marker))
        {
            return;
        }

        var previous = File.ReadAllText(marker).Trim();
        if (Directory.Exists(previous) && (!File.Exists(paths.DatabasePath) || !File.Exists(paths.MasterKeyPath)))
        {
            foreach (var name in new[] { OadmPaths.DatabaseFileName, OadmPaths.DatabaseFileName + "-wal", OadmPaths.DatabaseFileName + "-shm", OadmPaths.MasterKeyFileName })
            {
                var from = Path.Combine(previous, name);
                if (File.Exists(from))
                {
                    File.Move(from, Path.Combine(paths.DataDirectory, name), overwrite: true);
                }
            }

            var pluginData = Path.Combine(previous, "plugin-data");
            if (Directory.Exists(pluginData) && !Directory.Exists(paths.PluginDataDirectory))
            {
                Directory.Move(pluginData, paths.PluginDataDirectory);
            }

            LogSwapRecovered(previous);
        }

        File.Delete(marker);
    }

    /// <summary>The outcome of the last applied restore that the server has not recorded yet (<see cref="ForgetOutcome"/> after).</summary>
    public RestoreOutcome? ReadOutcome()
    {
        var path = Path.Combine(RestoreDirectory, ResultFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreOutcome>(File.ReadAllText(path), BackupFile.Json);
        }
        catch (JsonException)
        {
            File.Delete(path);
            return null;
        }
    }

    /// <summary>The outcome is recorded (report, audit).</summary>
    public void ForgetOutcome() => File.Delete(Path.Combine(RestoreDirectory, ResultFile));

    private static void Move(string from, string to, List<(string From, string To)>? moved)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
        else
        {
            return;
        }

        moved?.Add((from, to));
    }

    private void PrunePrevious()
    {
        foreach (var old in Directory.GetDirectories(RestoreDirectory, "previous-*").Order(StringComparer.Ordinal).SkipLast(KeepPrevious))
        {
            TryDelete(old);
        }
    }

    private static async Task<(BackupHeader Header, string ZipPath)> DecryptToFileAsync(Stream input, string password, string work, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "body.zip");
        var (header, body) = BackupFile.Open(input, password);
        await using (body.ConfigureAwait(false))
        {
            var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await body.CopyToAsync(file, ct).ConfigureAwait(false);
            }
        }

        return (header, zipPath);
    }

    private static BackupManifest ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(ManifestEntry) ?? throw new BackupFileException("The backup has no contents list.");
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(stream, BackupFile.Json) ?? new BackupManifest();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new BackupFileException("The backup file is damaged.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Left for the next cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Left for the next cleanup.
        }
    }

    /// <summary>Removes working files of an interrupted backup or restore check (server start).</summary>
    public void CleanTemp() => TryDelete(TempDirectory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup: {File} is in use and was left out")]
    private partial void LogFileSkipped(Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restored the backup {Source}; the replaced data is in {Previous}")]
    private partial void LogApplied(string source, string previous);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the backup failed; the server keeps its data (replaced files, if any, are in {Previous})")]
    private partial void LogApplyFailed(Exception ex, string previous);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the backup failed and {Path} could not be put back; the server's previous files are in the restore folder")]
    private partial void LogMoveBackFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A restore was interrupted while files were moved; the server's own files were put back from {Previous}")]
    private partial void LogSwapRecovered(string previous);
}
