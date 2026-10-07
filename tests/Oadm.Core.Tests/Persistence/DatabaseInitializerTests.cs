using Microsoft.EntityFrameworkCore;
using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

public class DatabaseInitializerTests
{
    [Fact]
    public async Task CreatesDatabaseMasterKeyAndFolders()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.True(File.Exists(db.Paths.DatabasePath));
        Assert.True(File.Exists(db.Paths.MasterKeyPath));
        Assert.Equal(32, new FileInfo(db.Paths.MasterKeyPath).Length);
        Assert.True(Directory.Exists(db.Paths.PluginsDirectory));
        Assert.True(Directory.Exists(db.Paths.LogsDirectory));

        await using var ctx = await db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
        Assert.Contains(await ctx.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondStartReusesDatabaseAndKey()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var first = await TestDatabase.CreateAsync(dir);
            var key = await File.ReadAllBytesAsync(first.Paths.MasterKeyPath);
            await first.CloseAsync();

            var second = await TestDatabase.CreateAsync(dir);
            Assert.Equal(key, await File.ReadAllBytesAsync(second.Paths.MasterKeyPath));
            await second.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void ModelHasNoPendingChanges()
    {
        using var ctx = new OadmDbContextDesignTimeFactory().CreateDbContext([]);
        Assert.False(ctx.Database.HasPendingModelChanges(), "Model changed: add an EF migration.");
    }
}
