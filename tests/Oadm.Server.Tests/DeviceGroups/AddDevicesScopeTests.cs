using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Sdk.Devices;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Sites;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// Adding devices with a device group (docs/spec/device-scope.md, Decisions "New devices", "Credential list", "Add and
/// discovery sessions", "Membership"): only at the group's sites, never with the credential list, only the user's own
/// sessions, only devices that land inside the group, the waiting list scoped by site and tried as the user who kept it.
/// Two sites both have 192.168.0.20 with different cameras, and the server's own network has a third one there.
/// </summary>
public sealed class AddDevicesScopeTests
{
    private const string LocalCamera = "ACCC8E0000C1";
    private const string CameraA = "ACCC8E0000A1";
    private const string CameraB = "ACCC8E0000B1";
    private const string Password = "pass-W0rd";

    [Fact]
    public async Task AGroupWithThisServerFindsAndAddsOnlyOnTheServersNetworkAndTheDeviceRemembersTheGroup()
    {
        await using var world = await World.StartAsync();
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true });

        var session = await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var row = Assert.Single((await SiteDiscoveryTests.WatchAsync(local.Discovery, session.SessionId)).Devices);
        Assert.Equal((LocalCamera, ""), (row.Serial, row.SiteId));
        Assert.Equal(0, world.Network.AtSite(world.A.Id)["192.168.0.20"].Requests + world.Network.AtSite(world.B.Id)["192.168.0.20"].Requests);

        var reply = await local.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { LocalCamera } });
        var device = await world.Repository.GetAsync(Guid.Parse(Assert.Single(reply.DeviceIds)), CancellationToken.None);
        Assert.False(device!.AddedByAllDevices);
        Assert.Equal(local.GroupId, device.AddedByGroupId);
        Assert.False(((IDeviceInfo)device).AddedByAllDevices); // what PKI and EST read for host names
        Assert.True((await world.Views.ForScopeAsync(DeviceScope.Group(local.GroupId), null, CancellationToken.None)).Contains(device));

        // The administrator finds all three, and what an administrator adds says so.
        var admin = await world.Admin.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var rows = (await SiteDiscoveryTests.WatchAsync(world.Admin, admin.SessionId)).Devices;
        Assert.Equal([CameraA, CameraB, LocalCamera], rows.Select(r => r.Serial).Order(StringComparer.Ordinal));
        var added = await world.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = admin.SessionId, DiscoveredIds = { CameraA } });
        var byAdmin = await world.Repository.GetAsync(Guid.Parse(Assert.Single(added.DeviceIds)), CancellationToken.None);
        Assert.True(((IDeviceInfo)byAdmin!).AddedByAllDevices);
        Assert.Null(byAdmin.AddedByGroupId);
    }

    [Fact]
    public async Task AGroupWithASiteFindsOnlyAtItsSiteAndAnotherSiteIsUnknown()
    {
        await using var world = await World.StartAsync();
        var a = await world.GroupUserAsync("a", new Proto.DeviceGroupRules { SiteIds = { world.A.Id.ToString() } });

        var session = await a.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var row = Assert.Single((await SiteDiscoveryTests.WatchAsync(a.Discovery, session.SessionId)).Devices);
        Assert.Equal((CameraA, "Building A"), (row.Serial, row.SiteName));
        Assert.Equal(0, world.Network["192.168.0.20"].Requests);

        var other = await Assert.ThrowsAsync<RpcException>(async () =>
            await a.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20", Site = "Building B" }));
        Assert.Equal((StatusCode.InvalidArgument, "There is no site named Building B."), (other.StatusCode, other.Status.Detail));

        // A range scan never scans the server's own network for this group.
        var scan = await a.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "192.168.0.20", To = "192.168.0.20" });
        var scanned = await SiteDiscoveryTests.WatchAsync(a.Discovery, scan.SessionId);
        Assert.All(scanned.Devices, d => Assert.Equal(world.A.Id.ToString(), d.SiteId));
        Assert.Equal(0, world.Network["192.168.0.20"].Requests);
    }

    [Fact]
    public async Task AGroupOfTagsOnlyCannotAddDevices()
    {
        await using var world = await World.StartAsync();
        var tags = await world.GroupUserAsync("tags", new Proto.DeviceGroupRules { Serials = { CameraA } });

        foreach (var call in new Func<Task>[]
        {
            async () => await tags.Discovery.StartZeroConfAsync(new Proto.Empty()),
            async () => await tags.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "192.168.0.20", To = "192.168.0.20" }),
            async () => await tags.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" }),
            async () => await tags.Waiting.AddAsync(new Proto.AddWaitingRequest { Lines = { new Proto.WaitingLine { Address = "192.168.0.20" } } }),
        })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal((StatusCode.PermissionDenied, AddDeviceTexts.CannotAdd), (refused.StatusCode, refused.Status.Detail));
        }

        Assert.Equal(0, world.Network["192.168.0.20"].Requests);
    }

    [Fact]
    public async Task SessionsBelongToTheUserWhoStartedThem()
    {
        await using var world = await World.StartAsync();
        world.Network.Add("10.9.0.5", FakeSerials.Make(5), Password);
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true });
        var other = await world.GroupUserAsync("other", new Proto.DeviceGroupRules { ThisServer = true });

        // The administrator's session: the device's login failed (no credential list).
        var adminSession = (await world.Admin.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.5" })).SessionId;
        Assert.Equal(Proto.AuthState.LoginFailed, Assert.Single((await SiteDiscoveryTests.WatchAsync(world.Admin, adminSession)).Devices).AuthState);
        var own = (await other.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.5" })).SessionId;
        await SiteDiscoveryTests.WatchAsync(other.Discovery, own);
        var requests = world.Network["10.9.0.5"].CredentialRequests.Count;

        foreach (var foreign in new[] { adminSession, own })
        {
            var watch = await Assert.ThrowsAsync<RpcException>(async () => await SiteDiscoveryTests.WatchAsync(local.Discovery, foreign));
            Assert.Equal(StatusCode.NotFound, watch.StatusCode);
            var commit = await Assert.ThrowsAsync<RpcException>(async () =>
                await local.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = foreign, DiscoveredIds = { FakeSerials.Make(5) } }));
            Assert.Equal(StatusCode.NotFound, commit.StatusCode);
            var retry = await Assert.ThrowsAsync<RpcException>(async () => await local.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
            {
                SessionId = foreign, DiscoveredId = FakeSerials.Make(5), UserName = "root", Password = Password,
            }));
            Assert.Equal(StatusCode.NotFound, retry.StatusCode);
            await local.Discovery.StopAsync(new Proto.DiscoverySession { SessionId = foreign }); // ignored
        }

        Assert.Equal(requests, world.Network["10.9.0.5"].CredentialRequests.Count);

        // A Retry of the user's own session never reaches into another user's session (related_session_ids).
        world.Network.Add("10.9.0.6", FakeSerials.Make(6), Password);
        var mine = (await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.6" })).SessionId;
        await SiteDiscoveryTests.WatchAsync(local.Discovery, mine);
        var ok = await local.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = mine, DiscoveredId = FakeSerials.Make(6), UserName = "root", Password = Password, RelatedSessionIds = { adminSession, own },
        });
        Assert.Equal(Proto.AuthState.Authenticated, ok.AuthState);
        Assert.Equal(requests, world.Network["10.9.0.5"].CredentialRequests.Count);

        // The administrator's session still works for the administrator after the other user's Stop.
        Assert.Single((await SiteDiscoveryTests.WatchAsync(world.Admin, adminSession)).Devices);
    }

    [Fact]
    public async Task TheCredentialListIsNeverSentToADeviceForAGroupUser()
    {
        await using var world = await World.StartAsync();
        var camera = world.Network.Add("10.9.0.5", FakeSerials.Make(5), Password);
        world.Network.Add("10.9.0.7", FakeSerials.Make(7), Password);
        await world.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true });

        // Add manually and a range scan: no password at all goes to the device.
        var session = (await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.5" })).SessionId;
        var row = Assert.Single((await SiteDiscoveryTests.WatchAsync(local.Discovery, session)).Devices);
        Assert.Equal((Proto.AuthState.LoginFailed, DiscoveryAuthenticator.NoTypedCredentialsText), (row.AuthState, row.AuthDetail));
        var scan = await local.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "10.9.0.5", To = "10.9.0.7" });
        Assert.All((await SiteDiscoveryTests.WatchAsync(local.Discovery, scan.SessionId)).Devices, d => Assert.Equal(Proto.AuthState.LoginFailed, d.AuthState));
        Assert.Empty(camera.CredentialRequests);
        Assert.Empty(world.Network["10.9.0.7"].CredentialRequests);

        // A credential added to the list meanwhile is not tried for the group user's sessions either.
        await world.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "admin", Password = "another" });
        await SiteDiscoveryTests.WatchAsync(local.Discovery, session);
        Assert.Empty(camera.CredentialRequests);

        // The waiting list: a line kept without a login is tried without any password.
        await local.Waiting.AddAsync(new Proto.AddWaitingRequest { Lines = { new Proto.WaitingLine { Address = "10.9.0.5" } } });
        await world.Host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: true, CancellationToken.None);
        Assert.Empty(camera.CredentialRequests);
        Assert.True(Assert.Single((await local.Waiting.ListAsync(new Proto.ListWaitingRequest())).Devices).LoginFailed);

        // What the user types works; the administrator's session uses the list as before.
        var typed = await local.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = FakeSerials.Make(5), UserName = "root", Password = Password });
        Assert.Equal((Proto.AuthState.Authenticated, "entered"), (typed.AuthState, typed.CredentialId));
        var line = (await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.7", UserName = "root", Password = Password })).SessionId;
        Assert.Equal(Proto.AuthState.Authenticated, Assert.Single((await SiteDiscoveryTests.WatchAsync(local.Discovery, line)).Devices).AuthState);
        Assert.All(camera.CredentialRequests.Concat(world.Network["10.9.0.7"].CredentialRequests), r => Assert.Equal("root", r.User));
        var admin = (await world.Admin.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.5" })).SessionId;
        Assert.StartsWith("list:", Assert.Single((await SiteDiscoveryTests.WatchAsync(world.Admin, admin)).Devices).CredentialId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManagedDeviceOutsideTheViewIsSentOnlyAsAlreadyAdded()
    {
        await using var world = await World.StartAsync();
        world.Network.Add("10.9.0.8", FakeSerials.Make(8), null, "P3265-V");
        world.Network.Add("10.9.0.9", FakeSerials.Make(9), null, "Q6135-LE");
        foreach (var address in new[] { "10.9.0.8", "10.9.0.9" })
        {
            var s = (await world.Admin.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = address })).SessionId;
            var found = Assert.Single((await SiteDiscoveryTests.WatchAsync(world.Admin, s)).Devices);
            await world.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = s, DiscoveredIds = { found.DiscoveredId } });
        }

        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true, ExceptSerials = { FakeSerials.Make(8) } });
        var scan = await local.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "10.9.0.8", To = "10.9.0.9" });
        var rows = (await SiteDiscoveryTests.WatchAsync(local.Discovery, scan.SessionId)).Devices.ToDictionary(d => d.Serial);

        var hidden = rows[FakeSerials.Make(8)];
        Assert.True(hidden.AlreadyManaged);
        Assert.Equal(Proto.AuthState.AlreadyAdded, hidden.AuthState);
        Assert.Equal(("", "", "", ""), (hidden.Model, hidden.Address, hidden.HostName, hidden.ProductType));
        var visible = rows[FakeSerials.Make(9)];
        Assert.True(visible.AlreadyManaged);
        Assert.Equal(("Q6135-LE", "10.9.0.9"), (visible.Model, visible.Address));
    }

    [Fact]
    public async Task AGroupUserNeverAddsADeviceThatWouldNotBeInTheGroup()
    {
        await using var world = await World.StartAsync();
        world.Network.Add("10.9.0.8", FakeSerials.Make(8), null);
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true, ExceptSerials = { FakeSerials.Make(8) } });

        var session = (await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.8" })).SessionId;
        Assert.Single((await SiteDiscoveryTests.WatchAsync(local.Discovery, session)).Devices);
        var reply = await local.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { FakeSerials.Make(8) } });

        Assert.Empty(reply.DeviceIds);
        Assert.Equal("", Assert.Single(reply.Results).DeviceId);
        Assert.Null(await world.Repository.FindBySerialAsync(FakeSerials.Make(8), CancellationToken.None));
    }

    [Fact]
    public async Task ARemovedSerialDoesNotJoinAGroupWhenReAddedBySomeoneOutsideIt()
    {
        await using var world = await World.StartAsync();
        var serial = FakeSerials.Make(8);
        world.Network.Add("10.9.0.8", serial, null);
        var single = await world.GroupUserAsync("single", new Proto.DeviceGroupRules { Serials = { serial } });
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true });

        async Task<Device> AddAsync(Proto.DiscoveryService.DiscoveryServiceClient discovery, Proto.AddDevicesService.AddDevicesServiceClient add)
        {
            var session = (await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.8" })).SessionId;
            await SiteDiscoveryTests.WatchAsync(discovery, session);
            var reply = await add.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { serial } });
            return (await world.Repository.GetAsync(Guid.Parse(Assert.Single(reply.DeviceIds)), CancellationToken.None))!;
        }

        async Task<bool> InSingleAsync(Device device) =>
            (await world.Views.ForScopeAsync(DeviceScope.Group(single.GroupId), null, CancellationToken.None)).Contains(device);

        var first = await AddAsync(world.Admin, world.AddDevices);
        Assert.True(await InSingleAsync(first));
        await world.Repository.RemoveAsync(first.Id, CancellationToken.None);

        // Added again by a user of another group: the serial of "single" stays dormant.
        var again = await AddAsync(local.Discovery, local.AddDevices);
        Assert.Equal(local.GroupId, again.AddedByGroupId);
        Assert.False(await InSingleAsync(again));
        await world.Repository.RemoveAsync(again.Id, CancellationToken.None);

        // Added by a user with all devices: it counts again.
        Assert.True(await InSingleAsync(await AddAsync(world.Admin, world.AddDevices)));
    }

    [Fact]
    public async Task TheWaitingListIsScopedBySiteAndTriesAsTheUserWhoKeptIt()
    {
        await using var world = await World.StartAsync();
        world.Network.AtSite(world.A.Id).Add("192.168.0.31", FakeSerials.Make(31), Password);
        var later = world.Network.AtSite(world.A.Id).Add("192.168.0.32", FakeSerials.Make(32), Password);
        var a = await world.GroupUserAsync("a", new Proto.DeviceGroupRules { SiteIds = { world.A.Id.ToString() } });
        var adminWaiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(world.Host.Invoker);
        await adminWaiting.AddAsync(new Proto.AddWaitingRequest
        {
            Lines = { new Proto.WaitingLine { Address = "192.168.0.30", SiteId = world.B.Id.ToString() }, new Proto.WaitingLine { Address = "10.9.0.40" } },
        });

        // The user's lines without a site wait for the group's only site.
        await a.Waiting.AddAsync(new Proto.AddWaitingRequest
        {
            Lines =
            {
                new Proto.WaitingLine { Address = "192.168.0.31", UserName = "root", Password = Password },
                new Proto.WaitingLine { Address = "192.168.0.32", UserName = "root", Password = Password },
            },
        });
        var mine = await a.Waiting.ListAsync(new Proto.ListWaitingRequest());
        Assert.Equal(2, mine.TotalCount);
        Assert.All(mine.Devices, d => Assert.Equal(world.A.Id.ToString(), d.SiteId));
        Assert.Equal(4, (await adminWaiting.ListAsync(new Proto.ListWaitingRequest())).TotalCount);
        var tries = await world.Host.Get<WaitingDeviceStore>().ToTryAsync(null, true, CancellationToken.None);
        Assert.All(tries.Where(t => t.SiteId == world.A.Id), t => Assert.Equal(a.UserId, t.QueuedByUserId));

        // Another site's line and another user's entries: refused whole, like unknown ones.
        var outside = await Assert.ThrowsAsync<RpcException>(async () => await a.Waiting.AddAsync(new Proto.AddWaitingRequest
        {
            Lines = { new Proto.WaitingLine { Address = "192.168.0.33", SiteId = world.B.Id.ToString() } },
        }));
        Assert.Equal(StatusCode.NotFound, outside.StatusCode);
        var adminIds = (await adminWaiting.ListAsync(new Proto.ListWaitingRequest())).Devices.Where(d => d.SiteId != world.A.Id.ToString()).Select(d => d.Id).ToList();
        foreach (var id in adminIds)
        {
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () =>
                await a.Waiting.RemoveAsync(new Proto.WaitingIds { Ids = { id, mine.Devices[0].Id } }))).StatusCode);
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () =>
                await a.Waiting.TryNowAsync(new Proto.WaitingIds { Ids = { id } }))).StatusCode);
        }

        Assert.Equal(4, (await adminWaiting.ListAsync(new Proto.ListWaitingRequest())).TotalCount);

        // A round adds the user's device into the group.
        var first = mine.Devices.Single(d => d.Address == "192.168.0.31").Id;
        Assert.Equal(1, await world.Host.Get<WaitingDeviceRunner>().RunRoundAsync([Guid.Parse(first)], true, CancellationToken.None));
        var added = await world.Repository.FindBySerialAsync(FakeSerials.Make(31), CancellationToken.None);
        Assert.Equal((false, (Guid?)a.GroupId, (Guid?)world.A.Id), (added!.AddedByAllDevices, added.AddedByGroupId, added.SiteId));

        // The user's group changes to one without that site: the other line is not tried, nothing is sent to its address.
        var groups = new Proto.DeviceGroupService.DeviceGroupServiceClient(world.Host.Invoker);
        var b = await groups.SaveAsync(new Proto.SaveDeviceGroupRequest { Name = "b only", Rules = new Proto.DeviceGroupRules { SiteIds = { world.B.Id.ToString() } } });
        await world.Host.Users.UpdateAsync(new Proto.UpdateUserRequest { Id = a.UserId.ToString(), AllDevices = false, DeviceGroupId = b.Id });
        var second = mine.Devices.Single(d => d.Address == "192.168.0.32").Id;
        Assert.Equal(0, await world.Host.Get<WaitingDeviceRunner>().RunRoundAsync([Guid.Parse(second)], true, CancellationToken.None));
        Assert.Equal(0, later.Requests);
        var held = (await adminWaiting.ListAsync(new Proto.ListWaitingRequest())).Devices.Single(d => d.Id == second);
        Assert.Equal(AddDeviceTexts.KeeperCannotAdd, held.LastResult);
        Assert.True(held.Held);
    }

    [Fact]
    public async Task AnAddIsAuditedAsEnteringTheGroupsOfTheDevice()
    {
        await using var world = await World.StartAsync();
        var local = await world.GroupUserAsync("local", new Proto.DeviceGroupRules { ThisServer = true });
        await world.GroupUserAsync("sitea", new Proto.DeviceGroupRules { SiteIds = { world.A.Id.ToString() } });

        var session = (await local.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" })).SessionId;
        await SiteDiscoveryTests.WatchAsync(local.Discovery, session);
        await local.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { LocalCamera } });

        var entries = (await world.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Where(e => e.Action is DeviceGroupMembership.EnteredAction or DeviceGroupMembership.LeftAction).ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(("local", "local", $"1 device (added): {LocalCamera}"), (entry.UserName, entry.Target, entry.Detail));
    }

    [Fact]
    public void MembershipDetailNamesAtMostTwentyDevices()
    {
        var change = new DeviceGroupMembershipChange(Guid.NewGuid(), "B", true, [.. Enumerable.Range(1, 25).Select(FakeSerials.Make)]);
        var detail = DeviceGroupMembership.Detail(change, "moved");
        Assert.StartsWith("25 devices (moved): ACCC8E000001, ", detail, StringComparison.Ordinal);
        Assert.EndsWith("ACCC8E000014 and 5 more", detail, StringComparison.Ordinal);
    }

    /// <summary>The server with two fake sites and the cameras at 192.168.0.20 (one per site, one on the server's network).</summary>
    private sealed class World : IAsyncDisposable
    {
        private int _roles;

        private World(TestServerHost host, FakeSiteTransport sites, FakeSiteTransport.FakeSite a, FakeSiteTransport.FakeSite b, FakeAxisNetwork network)
        {
            Host = host;
            Sites = sites;
            A = a;
            B = b;
            Network = network;
        }

        public TestServerHost Host { get; }

        public FakeSiteTransport Sites { get; }

        public FakeSiteTransport.FakeSite A { get; }

        public FakeSiteTransport.FakeSite B { get; }

        public FakeAxisNetwork Network { get; }

        public DeviceRepository Repository => Host.Get<DeviceRepository>();

        public DeviceViewFactory Views => Host.Get<DeviceViewFactory>();

        public Proto.DiscoveryService.DiscoveryServiceClient Admin => Host.Discovery;

        public Proto.AddDevicesService.AddDevicesServiceClient AddDevices => Host.AddDevices;

        public static async Task<World> StartAsync()
        {
            var sites = new FakeSiteTransport();
            var a = sites.AddSite("Building A");
            var b = sites.AddSite("Building B");
            var network = new FakeAxisNetwork();
            network.Add("192.168.0.20", LocalCamera, null);
            network.AtSite(a.Id).Add("192.168.0.20", CameraA, null);
            network.AtSite(b.Id).Add("192.168.0.20", CameraB, null);
            var host = await TestServerHost.StartAsync(network, configureServices: s =>
            {
                s.AddSingleton<ISiteStates>(sites);
                s.AddSingleton<ISiteNetwork>(sites);
            });
            return new World(host, sites, a, b, network);
        }

        /// <summary>A user with every permission and a new device group with <paramref name="rules"/> (named like the user).</summary>
        public async Task<GroupUser> GroupUserAsync(string name, Proto.DeviceGroupRules rules)
        {
            var roles = new Proto.RoleService.RoleServiceClient(Host.Invoker);
            var keys = (await roles.GetMatrixAsync(new Proto.Empty())).Permissions.Select(p => p.Key);
            var roleName = "Everything " + Interlocked.Increment(ref _roles);
            var role = (await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = roleName, Permissions = { keys } } } }))
                .Roles.Single(r => r.Name == roleName).Id;
            var group = await new Proto.DeviceGroupService.DeviceGroupServiceClient(Host.Invoker).SaveAsync(new Proto.SaveDeviceGroupRequest { Name = name, Rules = rules });
            var user = await Host.Users.AddAsync(new Proto.AddUserRequest { UserName = name, Password = "pw", RoleId = role, AllDevices = false, DeviceGroupId = group.Id });
            var invoker = await Host.InvokerForRoleAsync(name, Guid.Parse(role));
            return new GroupUser(invoker, Guid.Parse(user.Id), Guid.Parse(group.Id));
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed record GroupUser(CallInvoker Invoker, Guid UserId, Guid GroupId)
    {
        public Proto.DiscoveryService.DiscoveryServiceClient Discovery => new(Invoker);

        public Proto.AddDevicesService.AddDevicesServiceClient AddDevices => new(Invoker);

        public Proto.WaitingDeviceService.WaitingDeviceServiceClient Waiting => new(Invoker);
    }
}
