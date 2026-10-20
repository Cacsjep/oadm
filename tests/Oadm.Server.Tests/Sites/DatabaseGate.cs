using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Persistence;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// Holds the database command that writes a chosen audit action until the test opens the gate, synchronously like SQLite
/// itself (a slow disk): code that writes under a lock keeps the lock for as long as the gate is closed.
/// </summary>
internal sealed class DatabaseGate : DbCommandInterceptor
{
    private volatile TaskCompletionSource _open = Opened();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _action;

    /// <summary>Completes when a held command arrived.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Holds the next audit entries with <paramref name="action"/> until <see cref="Open"/>.</summary>
    public void Hold(string action)
    {
        _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _action = action;
    }

    public void Open()
    {
        _action = null;
        _open.TrySetResult();
    }

    /// <summary>Replaces the server's database factory with one that has this gate.</summary>
    public void Install(IServiceCollection services)
    {
        services.RemoveAll<IDbContextFactory<OadmDbContext>>();
        services.AddSingleton<IDbContextFactory<OadmDbContext>>(sp => new Factory(new DbContextOptionsBuilder<OadmDbContext>()
            .UseSqlite(sp.GetRequiredService<OadmPaths>().ConnectionString)
            .AddInterceptors(SqlitePragmaInterceptor.Instance, this)
            .Options));
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Wait(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Wait(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Wait(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Wait(command);
        return ValueTask.FromResult(result);
    }

    // Synchronous on purpose: SQLite's async calls run synchronously too.
    private void Wait(DbCommand command)
    {
        var action = _action;
        if (action is null || !command.CommandText.Contains("AuditEntries", StringComparison.Ordinal)
            || !command.Parameters.Cast<DbParameter>().Any(p => action.Equals(p.Value as string, StringComparison.Ordinal)))
        {
            return;
        }

        _entered.TrySetResult();
        _open.Task.Wait(TimeSpan.FromSeconds(60));
    }

    private static TaskCompletionSource Opened()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    private sealed class Factory(DbContextOptions<OadmDbContext> options) : IDbContextFactory<OadmDbContext>
    {
        public OadmDbContext CreateDbContext() => new(options);
    }
}
