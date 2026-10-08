using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oadm.Core.Security;

namespace Oadm.Core.Persistence;

/// <summary>
/// Startup step: creates the data folders, applies EF migrations (creating the database on first
/// start), switches SQLite to WAL and checks the master key (<see cref="MasterKeyCheck"/>: a missing or
/// different key is replaced and the unreadable credentials removed). Call once before serving.
/// </summary>
public sealed partial class DatabaseInitializer(
    OadmPaths paths,
    IDbContextFactory<OadmDbContext> dbFactory,
    CredentialProtector protector,
    ILogger<DatabaseInitializer>? logger = null,
    TimeProvider? time = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;

    /// <summary>Set when this start had to replace the master key.</summary>
    public KeyReplacedNotice? KeyReplaced { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        paths.EnsureDirectories();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (pending.Count > 0)
        {
            LogApplyingMigrations(pending.Count, paths.DatabasePath);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }

        // WAL is persistent in the database file; lets readers run while the poller writes.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);

        // The protector loaded or created master.key; check it against the stored key check value.
        KeyReplaced = await MasterKeyCheck.RunAsync(dbFactory, protector, time ?? TimeProvider.System, _logger, ct).ConfigureAwait(false);
        LogReady(paths.DataDirectory);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} database migration(s) to {DatabasePath}")]
    private partial void LogApplyingMigrations(int count, string databasePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Data folder ready at {DataDirectory}")]
    private partial void LogReady(string dataDirectory);
}
