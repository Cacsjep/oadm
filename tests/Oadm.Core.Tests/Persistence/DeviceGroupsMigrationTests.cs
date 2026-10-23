using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Persistence;
using Oadm.Core.Security;

namespace Oadm.Core.Tests.Persistence;

/// <summary>
/// The upgrade to device groups changes nobody's devices: every existing user gets All devices, every existing device counts
/// as added by a user with all devices, every existing event rule and compliance check is owned by all devices. The same
/// for the restore of a backup from before 4.2.
/// </summary>
public sealed class DeviceGroupsMigrationTests
{
    private const string Before = "20261023062817_DeviceArchitecture";

    /// <summary>A database as OADM 4.1 left it: an operator, a device, an event rule and a compliance check.</summary>
    private static async Task SeedOlderAsync(OadmDbContext db)
    {
        await db.GetService<IMigrator>().MigrateAsync(Before);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"Id\", \"UserName\", \"NormalizedName\", \"PasswordHash\", \"RoleId\", \"Disabled\", \"CreatedUtc\") " +
            "SELECT '11111111-1111-1111-1111-111111111111', 'tech', 'TECH', 'x', \"Id\", 0, '2026-01-01 00:00:00' FROM \"Roles\" WHERE \"Kind\" = 'Operator'");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Serial\", \"Address\", \"UseHostName\", \"Status\", \"Scheme\", \"Category\", \"CertTrust\", \"Tags\", \"Apis\") " +
            "VALUES ('22222222-2222-2222-2222-222222222222', 'ACCC8E000001', '10.0.0.1', 0, 'Ok', 'Https', 'Camera', 'Unknown', '[]', '[]')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"EventRules\" (\"Id\", \"Name\", \"Enabled\", \"DefinitionJson\", \"CreatedUtc\", \"UpdatedUtc\") " +
            "VALUES ('33333333-3333-3333-3333-333333333333', 'Old rule', 1, '{{}}', '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ComplianceChecks\" (\"Id\", \"Name\", \"TypeId\", \"SettingsJson\", \"DevicesJson\", \"Severity\", \"IntervalMinutes\", \"Enabled\", \"BuiltIn\", \"Version\", \"CreatedUtc\", \"ChangedUtc\") " +
            "VALUES ('44444444-4444-4444-4444-444444444444', 'Old check', 't', '{{}}', '{{}}', 0, 0, 1, 0, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00')");
    }

    private static async Task AssertNobodyLostDevicesAsync(OadmDbContext db)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.UserName == "tech");
        Assert.True(user.AllDevices);
        Assert.Null(user.DeviceGroupId);
        Assert.Equal(DeviceScope.All, user.DeviceAccess);
        Assert.True((await db.Devices.AsNoTracking().SingleAsync()).AddedByAllDevices);
        Assert.True((await db.EventRules.AsNoTracking().SingleAsync()).Enabled); // rules have no owner (EventRulesWithoutOwner)
        var ruleColumns = await db.Database.SqlQueryRaw<string>("SELECT \"name\" AS \"Value\" FROM pragma_table_info('EventRules')").ToListAsync();
        Assert.DoesNotContain("OwnerAllDevices", ruleColumns);
        Assert.DoesNotContain("OwnerGroupId", ruleColumns);
        Assert.True((await db.ComplianceChecks.AsNoTracking().SingleAsync()).Owner.AllDevices);
        Assert.Empty(await db.DeviceGroups.ToListAsync());
    }

    [Fact]
    public async Task TheMigrationGivesEveryExistingUserAllDevices()
    {
        var dir = TestDatabase.NewTempDirectory();
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<OadmDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dir, "oadm.db")};Pooling=False")
                .Options;
            await using (var before = new OadmDbContext(options))
            {
                await SeedOlderAsync(before);
            }

            await using var after = new OadmDbContext(options);
            await after.Database.MigrateAsync();
            await AssertNobodyLostDevicesAsync(after);

            // A user added afterwards chooses explicitly: the column has no database default for it.
            after.Users.Add(new UserEntity { Id = Guid.NewGuid(), UserName = "new", NormalizedName = "NEW", PasswordHash = "x", RoleId = BuiltInRoles.OperatorId });
            await after.SaveChangesAsync();
            Assert.False((await after.Users.AsNoTracking().SingleAsync(u => u.UserName == "new")).AllDevices);
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task ABackupFromBeforeDeviceGroupsRestoresWithEveryUserOnAllDevices()
    {
        var sourceDir = TestDatabase.NewTempDirectory();
        var targetDir = TestDatabase.NewTempDirectory();
        try
        {
            var olderPaths = new OadmPaths(sourceDir);
            olderPaths.EnsureDirectories();
            using (var provider = new ServiceCollection().AddOadmPersistence(olderPaths).BuildServiceProvider())
            {
                await using var db = await provider.GetRequiredService<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
                await SeedOlderAsync(db);
                _ = provider.GetRequiredService<CredentialProtector>(); // writes master.key
            }

            SqliteConnection.ClearAllPools();
            var file = new MemoryStream();
            var header = new BackupHeader { Kind = "manual", OadmVersion = "4.1.0", ServerName = "site-a" };
            var contents = await new BackupArchive(olderPaths) { Iterations = 1000 }.CreateAsync(file, "correct horse", header, [], CancellationToken.None);
            Assert.Equal(Before, contents.Header.Migration);

            var target = await TestDatabase.CreateAsync(targetDir);
            await target.CloseAsync();
            var archive = new BackupArchive(target.Paths);
            file.Position = 0;
            await archive.StageAsync(file, "correct horse", "old.oadmbackup", "admin", "", CancellationToken.None);
            Assert.Null(archive.ApplyPending()!.Error);

            var restored = await TestDatabase.CreateAsync(targetDir);
            await using (var ctx = await restored.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
            {
                Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
                await AssertNobodyLostDevicesAsync(ctx);
            }

            // The restored operator's view is every device.
            var user = Assert.Single(await restored.Get<UserStore>().ListAsync(CancellationToken.None));
            var view = await restored.Get<DeviceViewFactory>().ForScopeAsync(user.DeviceAccess, user.UserName, CancellationToken.None);
            Assert.True(view.HasAllDevices);
            Assert.Single(await view.ListAsync(CancellationToken.None));
            await restored.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(sourceDir);
            TestDatabase.DeleteDirectory(targetDir);
        }
    }
    [Fact]
    public async Task TheFlagsOfDeviceGroupsHaveNoDatabaseDefaultSoARawInsertFailsClosed()
    {
        var dir = TestDatabase.NewTempDirectory();
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<OadmDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dir, "oadm.db")};Pooling=False")
                .Options;
            await using (var before = new OadmDbContext(options))
            {
                await SeedOlderAsync(before);
                await before.GetService<IMigrator>().MigrateAsync("20261023092453_EventRulesWithoutOwner");
            }

            await using var after = new OadmDbContext(options);
            await after.Database.MigrateAsync();

            // The rebuilt tables keep their rows and values.
            await AssertNobodyLostDevicesAsync(after);

            foreach (var (table, column) in new[] { ("Users", "AllDevices"), ("Devices", "AddedByAllDevices"), ("ComplianceChecks", "OwnerAllDevices") })
            {
                var sql = "SELECT COALESCE(\"dflt_value\", 'none') AS \"Value\" FROM pragma_table_info('" + table + "') WHERE \"name\" = '" + column + "'";
                var defaults = await after.Database.SqlQueryRaw<string>(sql).ToListAsync();
                Assert.Equal(["none"], defaults);
            }

            // A row inserted without its flag is refused, never given all devices; with the flag (the only missing column) it goes in.
            var userId = BuiltInRoles.OperatorId.ToString();
            var inserts = new (string Table, string Flag, string Columns, string Values)[]
            {
                ("Users", "AllDevices", "\"Id\", \"UserName\", \"NormalizedName\", \"PasswordHash\", \"RoleId\", \"Disabled\", \"CreatedUtc\"",
                    "'55555555-5555-5555-5555-555555555555', 'raw', 'RAW', 'x', '" + userId + "', 0, '2026-01-01 00:00:00'"),
                ("Devices", "AddedByAllDevices", "\"Id\", \"Serial\", \"Address\", \"UseHostName\", \"Status\", \"Scheme\", \"Category\", \"CertTrust\", \"Tags\", \"Apis\"",
                    "'66666666-6666-6666-6666-666666666666', 'ACCC8E000002', '10.0.0.2', 0, 'Ok', 'Https', 'Camera', 'Unknown', '[]', '[]'"),
                ("ComplianceChecks", "OwnerAllDevices", "\"Id\", \"Name\", \"TypeId\", \"SettingsJson\", \"DevicesJson\", \"Severity\", \"IntervalMinutes\", \"Enabled\", \"BuiltIn\", \"Version\", \"CreatedUtc\", \"ChangedUtc\"",
                    "'77777777-7777-7777-7777-777777777777', 'Raw check', 't', '{{}}', '{{}}', 0, 0, 1, 0, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00'"),
            };
            foreach (var (table, flag, columns, values) in inserts)
            {
                var without = "INSERT INTO \"" + table + "\" (" + columns + ") VALUES (" + values + ")";
                var error = await Assert.ThrowsAsync<SqliteException>(() => after.Database.ExecuteSqlRawAsync(without));
                Assert.Contains(flag, error.Message, StringComparison.Ordinal);
                var with = "INSERT INTO \"" + table + "\" (" + columns + ", \"" + flag + "\") VALUES (" + values + ", 0)";
                await after.Database.ExecuteSqlRawAsync(with);
            }

            Assert.False((await after.Users.AsNoTracking().SingleAsync(u => u.UserName == "raw")).AllDevices);
            Assert.Equal(2, await after.Devices.CountAsync());
            Assert.Equal(2, await after.ComplianceChecks.CountAsync());
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }
}
