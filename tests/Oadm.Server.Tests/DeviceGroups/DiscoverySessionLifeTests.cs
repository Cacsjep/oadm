using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Sdk.Testing;
using Oadm.Server.AddDevices;
using Oadm.Server.Discovery;
using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// An add page session lives while a watch of it is open and then 30 minutes after its last use; then it is stopped on the
/// server and its owner and logins (typed passwords, the credential list) are forgotten. A user's sessions stop at once on a
/// logout or an expired login, a new role or other devices, and new rules or the deletion of the user's group.
/// </summary>
public sealed class DiscoverySessionLifeTests
{
    private static readonly AuthenticatedCaller Anna = new(Guid.NewGuid(), "anna", BuiltInRoles.OperatorId, PermissionSet.All, "local", "", "h-anna");

    [Fact]
    public async Task ALoginRunningWhenItsSessionIsForgottenSendsNoFurtherCredential()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.7.1", FakeSerials.Make(71), "the-real-password");
        await using var host = await TestServerHost.StartAsync(network);
        foreach (var password in new[] { "wrong-1", "wrong-2", "wrong-3" })
        {
            await host.Settings.AddCredentialAsync(new Contracts.V1.AddCredentialRequest { UserName = "root", Password = password });
        }

        var authenticator = host.Get<DiscoveryAuthenticator>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logins = 0;
        network.BeforeLoginAnswer = async (_, ct) =>
        {
            if (Interlocked.Increment(ref logins) == 1)
            {
                held.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct); // until the session's login is cancelled
                }
                finally
                {
                    ended.TrySetResult();
                }
            }
        };
        authenticator.AllowCredentialList("stopped");
        authenticator.Ensure("stopped", new DiscoveredDevice(camera.Serial, camera.Serial, System.Net.IPAddress.Parse("10.9.7.1"), null, "P3265-V", "12.6.85",
            DiscoveredDeviceStatus.CredentialsRequired, "http", DiscoverySources.RangeScan, DateTimeOffset.UtcNow), alreadyManaged: false);
        await held.Task.WaitAsync(Eventually.DefaultTimeout);

        // The session is stopped for an access change while a login of the credential list waits for its answer.
        authenticator.Forget("stopped");
        await ended.Task.WaitAsync(Eventually.DefaultTimeout);
        await Eventually.TrueAsync(() => authenticator.RunningLogins == 0, "the login to end");

        Assert.Equal(1, Volatile.Read(ref logins));
    }

    [Fact]
    public async Task AWatchedSessionStaysAndAnIdleOneIsStoppedAndForgottenAfterTheLifetime()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider();
        var backend = new Sessions("watched", "idle");
        using var authenticator = new DiscoveryAuthenticator(
            host.Get<CredentialListStore>(), host.Get<IVapixConnector>(), time, NullLogger<DiscoveryAuthenticator>.Instance, sessions: backend);
        using var owners = new DiscoverySessionOwners(time, backend, authenticator);
        foreach (var id in new[] { "watched", "idle" })
        {
            authenticator.AllowCredentialList(id);
            Assert.True(owners.Register(id, Anna));
        }

        var other = Anna with { UserId = Guid.NewGuid() };
        Assert.Null(owners.Hold("watched", other)); // another user's session: nothing to hold
        var hold = owners.Hold("watched", Anna);
        Assert.NotNull(hold);

        time.Advance(DiscoverySessionOwners.Lifetime + (DiscoverySessionOwners.PruneEvery * 2));
        await Eventually.TrueAsync(() => backend.Stopped.Contains("idle"), "the idle session was stopped");
        Assert.False(owners.Owns("idle", Anna));
        Assert.False(authenticator.UsesCredentialList("idle")); // its logins went with it
        Assert.True(owners.Owns("watched", Anna));
        Assert.True(authenticator.UsesCredentialList("watched"));

        // The watch ended (the client crashed): one lifetime after the last use the session goes too.
        hold!.Dispose();
        time.Advance(DiscoverySessionOwners.Lifetime - TimeSpan.FromMinutes(5));
        Assert.True(owners.Owns("watched", Anna)); // a use moves the lifetime on
        time.Advance(DiscoverySessionOwners.Lifetime + (DiscoverySessionOwners.PruneEvery * 2));
        await Eventually.TrueAsync(() => backend.Stopped.Contains("watched"), "the watched session was stopped");
        Assert.False(owners.Owns("watched", Anna));
        Assert.False(authenticator.UsesCredentialList("watched"));
        Assert.Equal(0, owners.Count);
    }

    [Fact]
    public async Task AUsersSessionsStopOnLogoutNewAccessAndNewRulesOfTheGroup()
    {
        await using var host = await TestServerHost.StartAsync();
        var users = host.Get<UserStore>();
        var groups = host.Get<DeviceGroupStore>();
        var owners = host.Get<DiscoverySessionOwners>(); // the host's own: subscribed to the stores
        var authenticator = host.Get<DiscoveryAuthenticator>();
        var epochs = host.Get<AccessEpochs>();
        var tokens = host.Get<AuthTokenStore>();
        var group = (await groups.SaveAsync(null, "Lobby", "", new DeviceGroupRules(true, [], [], [], []), 0, CancellationToken.None)).Group;
        var anna = await users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), CancellationToken.None);
        var berta = await users.AddAsync("berta", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), CancellationToken.None);
        var annaEntity = (await users.FindByNameAsync("anna", CancellationToken.None))!;
        var annaFirst = await tokens.CreateAsync(annaEntity, false, "test", CancellationToken.None);
        var annaSecond = await tokens.CreateAsync(annaEntity, false, "test", CancellationToken.None);
        AuthenticatedCaller CallerOf(UserRecord user, string token) =>
            new(user.Id, user.UserName, user.RoleId, PermissionSet.All, "local", "", AuthTokenStore.HashOf(token))
            {
                Devices = new CallerDevices(user.DeviceAccess, groups.RevisionOf(group.Id)),
                AccessEpoch = epochs.Capture(),
            };

        var first = CallerOf(anna, annaFirst);
        var second = CallerOf(anna, annaSecond);
        var bertaCaller = CallerOf(berta, "berta-token");
        Assert.True(owners.Register("anna-1", first));
        Assert.True(owners.Register("anna-2", second));
        Assert.True(owners.Register("berta-1", bertaCaller));
        authenticator.AddSessionCredential("anna-1", "root", "typed-password");
        authenticator.AllowCredentialList("anna-1"); // only to see that the login session goes with it

        // A logout ends only the sessions of that login, with what was typed for them.
        await tokens.RevokeAsync(AuthTokenStore.HashOf(annaFirst), CancellationToken.None);
        await Eventually.TrueAsync(() => !owners.Owns("anna-1", first), "the logged out session stopped");
        Assert.False(authenticator.UsesCredentialList("anna-1"));
        Assert.True(owners.Owns("anna-2", second));

        // A new role ends the user's other sessions; another user's stay.
        await users.UpdateAsync(anna.Id, BuiltInRoles.ViewerId, null, null, null, null, CancellationToken.None);
        await Eventually.TrueAsync(() => !owners.Owns("anna-2", second), "the sessions of the new role stopped");
        Assert.True(owners.Owns("berta-1", bertaCaller));

        // New rules of the group end its users' sessions.
        await groups.SaveAsync(group.Id, "Lobby", "", new DeviceGroupRules(false, [], [], ["ACCC8E000009"], []), group.Revision, CancellationToken.None);
        await Eventually.TrueAsync(() => !owners.Owns("berta-1", bertaCaller), "the group's sessions stopped");
        Assert.Equal(0, owners.Count);

        // A call that began before the change cannot leave a session behind; the next call can.
        Assert.False(owners.Register("berta-late", bertaCaller));
        Assert.False(owners.Owns("berta-late", bertaCaller));
        Assert.True(owners.Register("berta-new", CallerOf(berta, "berta-token")));
    }

    [Fact]
    public async Task AnExpiredLoginStopsItsSessions()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tokens = new AuthTokenStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Core.Persistence.OadmDbContext>>(), time);
        var backend = new Sessions("s");
        using var owners = new DiscoverySessionOwners(new FakeTimeProvider(), backend, tokens: tokens); // the add page stays in use
        var admin = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var token = await tokens.CreateAsync(admin, false, "test", CancellationToken.None);
        var caller = new AuthenticatedCaller(admin.Id, admin.UserName, admin.RoleId, PermissionSet.All, "local", "", AuthTokenStore.HashOf(token));
        Assert.True(owners.Register("s", caller));

        time.Advance(AuthTokenStore.SessionLifetime + TimeSpan.FromMinutes(1));
        Assert.True(owners.Owns("s", caller)); // the login expired, nobody noticed yet
        await tokens.DeleteExpiredAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => backend.Stopped.Contains("s"), "the session of the expired login stopped");
        Assert.False(owners.Owns("s", caller));
    }

    [Fact]
    public async Task ALoginSessionOfAnExistingDiscoverySessionKeepsTheCredentialListAfterAnIdleHour()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider();
        var backend = new Sessions("open");
        using var authenticator = new DiscoveryAuthenticator(
            host.Get<CredentialListStore>(), host.Get<IVapixConnector>(), time, NullLogger<DiscoveryAuthenticator>.Instance, sessions: backend);
        authenticator.AllowCredentialList("open");
        authenticator.AllowCredentialList("ended");

        time.Advance(TimeSpan.FromHours(1));
        authenticator.SetExpectedSerial("another", "ACCC8E000001"); // any use of a session prunes the idle ones

        Assert.True(authenticator.UsesCredentialList("open"));
        Assert.False(authenticator.UsesCredentialList("ended")); // gone: a session made again tries only typed passwords
    }

    /// <summary>Which sessions exist, and which were stopped.</summary>
    private sealed class Sessions(params string[] active) : IDiscoveryBackend
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new(active.Select(a => new KeyValuePair<string, byte>(a, 0)));

        public System.Collections.Concurrent.ConcurrentQueue<string> Stopped { get; } = new();

        public bool IsActive(string sessionId) => _active.ContainsKey(sessionId);

        public Task<DiscoverySession> StartZeroConfAsync(CancellationToken ct, DiscoveryRoutes? routes = null) => throw new NotSupportedException();

        public void StopScan(string sessionId) => throw new NotSupportedException();

        public Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct, DiscoveryRoutes? routes = null) => throw new NotSupportedException();

        public Task<DiscoverySession> StartAddressProbeAsync(string input, bool importLine, CancellationToken ct, Guid? siteId = null, string? expectedSerial = null, DiscoveryRoutes? routes = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(string sessionId)
        {
            _active.TryRemove(sessionId, out _);
            Stopped.Enqueue(sessionId);
            return Task.CompletedTask;
        }

        public DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId) => throw new NotSupportedException();
    }
}
