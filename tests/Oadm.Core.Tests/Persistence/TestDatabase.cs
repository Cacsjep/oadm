using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

/// <summary>A migrated SQLite database in a temp data folder, wired through AddOadmPersistence.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestDatabase(OadmPaths paths, ServiceProvider provider)
    {
        Paths = paths;
        _provider = provider;
    }

    public OadmPaths Paths { get; }

    public IServiceProvider Services => _provider;

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public static string NewTempDirectory() =>
        Path.Combine(Path.GetTempPath(), "oadm-tests", Guid.NewGuid().ToString("N"));

    public static async Task<TestDatabase> CreateAsync(string? dataDirectory = null)
    {
        var paths = new OadmPaths(dataDirectory ?? NewTempDirectory());
        var provider = new ServiceCollection().AddOadmPersistence(paths).BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
        return new TestDatabase(paths, provider);
    }

    /// <summary>Disposes the services but keeps the folder (to reopen the same database).</summary>
    public async ValueTask CloseAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        DeleteDirectory(Paths.DataDirectory);
    }

    public static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
