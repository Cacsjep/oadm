using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>The membership audit never fails the add or the move it reports on (docs/spec/device-scope.md, Membership).</summary>
public sealed class MembershipAuditTests
{
    [Fact]
    public async Task AnAuditThatCannotBeWrittenNeverFailsTheAddOrTheMove()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await host.Get<DeviceRepository>().AddAsync(new Device { Serial = "ACCC8E000001", Address = "10.0.0.1" }, CancellationToken.None);
        await host.Get<DeviceGroupStore>().SaveAsync(null, "Singles", "", new DeviceGroupRules(false, [], [], ["ACCC8E000001"], []), 0, CancellationToken.None);
        var views = host.Get<DeviceViewFactory>();
        var broken = new AuditLog(new BrokenDatabase(), TimeProvider.System);

        await MembershipAudit.AddedAsync(views, broken, "anna", "pc", [device.Id], string.Empty, CancellationToken.None);
        var moved = device.Clone();
        moved.SiteId = Guid.NewGuid(); // it was at another route before
        await MembershipAudit.MovedAsync(views, broken, moved, CancellationToken.None);

        // The call's own cancel still ends it.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MembershipAudit.AddedAsync(views, broken, "anna", "pc", [device.Id], string.Empty, cancelled.Token));
    }

    /// <summary>A database that cannot be opened (a locked or broken file).</summary>
    private sealed class BrokenDatabase : IDbContextFactory<OadmDbContext>
    {
        public OadmDbContext CreateDbContext() => throw new InvalidOperationException("database is locked");
    }
}
