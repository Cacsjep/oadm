using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

/// <summary>The upgrade to roles: every user keeps what the old two-value role said.</summary>
public sealed class RolesMigrationTests
{
    [Fact]
    public async Task AdministratorsStayAdministratorsAndEveryoneElseBecomesOperator()
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
                // The database as the release before roles left it, with an administrator and an operator.
                await before.GetService<IMigrator>().MigrateAsync("20261010164441_EventRules");
                await before.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Users\" (\"Id\", \"UserName\", \"NormalizedName\", \"PasswordHash\", \"Role\", \"Disabled\", \"CreatedUtc\") VALUES " +
                    "('11111111-1111-1111-1111-111111111111', 'boss', 'BOSS', 'x', 'Admin', 0, '2026-01-01 00:00:00'), " +
                    "('22222222-2222-2222-2222-222222222222', 'tech', 'TECH', 'x', 'Operator', 0, '2026-01-01 00:00:00')");
            }

            await using var after = new OadmDbContext(options);
            await after.Database.MigrateAsync();

            var users = await after.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
            Assert.Equal(["boss", "tech"], users.Select(u => u.UserName));
            Assert.Equal(BuiltInRoles.AdministratorId, users[0].RoleId);
            Assert.Equal(BuiltInRoles.OperatorId, users[1].RoleId);

            var roles = await after.Roles.AsNoTracking().OrderBy(r => r.Kind).ToListAsync();
            Assert.Equal([RoleKind.Administrator, RoleKind.Operator, RoleKind.Viewer], roles.Select(r => r.Kind));
            Assert.Equal(["Administrator", "Operator", "Viewer"], roles.Select(r => r.Name));

            // A role in use cannot be removed under the users (Restrict).
            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            {
                after.Roles.Remove(await after.Roles.SingleAsync(r => r.Kind == RoleKind.Operator));
                await after.SaveChangesAsync();
            });
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task TheFirstStartGivesTheBuiltInRolesTheirDefaultsAndRemembersWhatOperatorsLost()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = db.Get<RoleStore>();

        var sync = await store.SyncCatalogAsync(PermissionCatalog.HostOnly, CancellationToken.None);
        Assert.True(sync.OperatorFirstSync);
        Assert.Contains(HostPermissions.TagsManage, sync.OperatorWithheld);
        Assert.DoesNotContain(HostPermissions.DevicesAddRemove, sync.OperatorWithheld);

        Assert.True((await store.PermissionsOfAsync(BuiltInRoles.OperatorId, CancellationToken.None)).Has(HostPermissions.DevicesAddRemove));
        Assert.True((await store.PermissionsOfAsync(BuiltInRoles.ViewerId, CancellationToken.None)).Has(HostPermissions.DevicesLiveView));
        Assert.False((await store.PermissionsOfAsync(BuiltInRoles.ViewerId, CancellationToken.None)).Has(HostPermissions.DevicesAddRemove));
        Assert.True((await store.PermissionsOfAsync(BuiltInRoles.AdministratorId, CancellationToken.None)).IsAll);
        Assert.False((await store.PermissionsOfAsync(Guid.NewGuid(), CancellationToken.None)).Has(HostPermissions.DevicesAddRemove));

        // The second start finds nothing new.
        var again = await store.SyncCatalogAsync(PermissionCatalog.HostOnly, CancellationToken.None);
        Assert.False(again.OperatorFirstSync);
        Assert.Empty(again.OperatorWithheld);
    }
}
