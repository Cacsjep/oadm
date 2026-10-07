using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Oadm.Core.Persistence;

/// <summary>
/// Sets per-connection SQLite pragmas. <c>synchronous=NORMAL</c>: with the WAL journal (set by
/// <see cref="DatabaseInitializer"/>) a commit no longer waits for an fsync; the database stays
/// consistent after a crash or power loss, only the last commits before a power loss may be lost.
/// Scale: a 60 s poll of 5,000 devices writes 5,000 small transactions per minute and a Run on 5,000
/// devices about 20,000; with FULL each one waits for the disk (about 4 ms each, measured 22 s for one
/// poll round of 5,000 devices).
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    private const string Pragmas = "PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Pragmas;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
