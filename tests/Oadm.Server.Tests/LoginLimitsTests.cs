using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// What a login may cost before anybody is authenticated: the length of its texts, what reaches the audit and the server
/// log, the failed logins of one address.
/// </summary>
public sealed class LoginLimitsTests
{
    private static async Task<RpcException> RefusedAsync(Proto.AuthService.AuthServiceClient auth, string userName, string password) =>
        await Assert.ThrowsAsync<RpcException>(async () => await auth.LoginAsync(new Proto.LoginRequest { UserName = userName, Password = password }));

    [Fact]
    public async Task ANameOrPasswordLongerThanAUserCanHaveIsAWrongLoginAndIsNeverKept()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "tech", Password = "Tech-pass-123", Role = Proto.UserRole.Operator });
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));

        var longName = await RefusedAsync(auth, "eve\r\n2026-11-06 08:00:00 [INF] User admin logged in" + new string('a', 200_000), "x");
        var longPassword = await RefusedAsync(auth, "tech", new string('p', 200_000));

        // The normal answer: nothing tells the caller that a limit was hit.
        Assert.All([longName, longPassword], e => Assert.Equal((StatusCode.Unauthenticated, AuthManager.WrongLoginMessage), (e.StatusCode, e.Status.Detail)));
        var entries = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.LoginFailed).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.InRange(e.UserName.Length, 1, UserStore.MaxUserNameLength);
            Assert.DoesNotContain(e.UserName + e.Target + e.Detail, char.IsControl);
        });
        Assert.Contains(entries, e => e.UserName == "tech" && e.Detail == "Wrong password");
    }

    [Fact]
    public async Task TheThrottleCountsALongNameByItsFirstCharacters()
    {
        await using var host = await TestServerHost.StartAsync();
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));
        var start = new string('b', UserStore.MaxUserNameLength);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.Equal(StatusCode.Unauthenticated, (await RefusedAsync(auth, start + "-tail-" + i, "x")).StatusCode);
        }

        // Five different tails were one name for the throttle: it holds 64 characters of a name, never megabytes.
        Assert.Equal(StatusCode.ResourceExhausted, (await RefusedAsync(auth, start + "-another-tail", "x")).StatusCode);
    }

    [Fact]
    public async Task UnknownNamesOfOneAddressAreOneAuditEntryAMinuteWithTheirCount()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var audit = host.Get<AuditLog>();
        using var manager = new AuthManager(host.Get<UserStore>(), host.Get<AuthTokenStore>(), host.Get<PasswordHasher>(), new LoginThrottle(time), audit, host.Get<OadmPaths>(), null, time);

        async Task FailAsync(string name, string address) =>
            await Assert.ThrowsAsync<AuthException>(() => manager.LoginAsync(name, "x", false, address, CancellationToken.None));
        async Task<List<AuditRecord>> FailedOfAsync(string address) =>
            [.. (await audit.ListAsync(0, 0, CancellationToken.None)).Entries.Where(e => e.Action == AuditActions.LoginFailed && e.ClientAddress == address).Reverse()];

        await FailAsync("ghost-0", "10.0.0.9");
        var first = Assert.Single(await FailedOfAsync("10.0.0.9"));
        Assert.Equal(("ghost-0", AuthManager.UnknownUserDetail), (first.Target, first.Detail));

        for (var i = 1; i < 12; i++)
        {
            await FailAsync("ghost-" + i, "10.0.0.9");
        }

        await FailAsync("ghost-x", "10.0.0.10"); // another address has its own entry
        var folded = Assert.Single(await FailedOfAsync("10.0.0.9"));
        Assert.Equal(("ghost-0", "12 failed logins with unknown user names"), (folded.Target, folded.Detail));
        Assert.Equal(AuthManager.UnknownUserDetail, Assert.Single(await FailedOfAsync("10.0.0.10")).Detail);

        // The next minute starts the next entry; the one before keeps its count.
        time.Advance(AuthManager.UnknownLoginsEntry);
        await FailAsync("ghost-late", "10.0.0.9");
        Assert.Equal(["12 failed logins with unknown user names", AuthManager.UnknownUserDetail], (await FailedOfAsync("10.0.0.9")).Select(e => e.Detail));
    }

    [Fact]
    public async Task AWrongPasswordOfAUserKeepsItsOwnAuditEntry()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "tech", Password = "Tech-pass-123", Role = Proto.UserRole.Operator });
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));
        await RefusedAsync(auth, "nobody", "x");
        await RefusedAsync(auth, "tech", "wrong-1");
        await RefusedAsync(auth, "tech", "wrong-2");

        var details = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.LoginFailed).Select(e => (e.UserName, e.Detail));
        Assert.Equal([("nobody", AuthManager.UnknownUserDetail), ("tech", "Wrong password"), ("tech", "Wrong password")], details.Reverse());
    }

    [Fact]
    public async Task FiftyFailedLoginsOfOneAddressRefuseItsLoginsUntilTheWindowEnds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await TestServerHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<LoginAddressLimit>();
            services.AddSingleton(new LoginAddressLimit(time));
        });
        await host.Users.AddAsync(new Proto.AddUserRequest { UserName = "tech", Password = "Tech-pass-123", Role = Proto.UserRole.Operator });
        var auth = new Proto.AuthService.AuthServiceClient(host.InvokerFor(null));
        for (var i = 0; i < LoginAddressLimit.MaxFailures; i++)
        {
            // A new name every time: no single name is ever locked.
            Assert.Equal(StatusCode.Unauthenticated, (await RefusedAsync(auth, "guess-" + i, "x")).StatusCode);
        }

        var refused = await RefusedAsync(auth, "tech", "Tech-pass-123"); // even the right password
        Assert.Equal((StatusCode.ResourceExhausted, LoginAddressLimit.Message), (refused.StatusCode, refused.Status.Detail));
        var entries = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var locked = Assert.Single(entries, e => e.Action == AuditActions.LoginLocked);
        Assert.Equal(("local", "50 failed logins from this address in 5 minutes"), (locked.Target, locked.Detail));

        // Signed-in users of the address keep working.
        Assert.Equal(TestServerHost.AdminUserName, (await host.Auth.MeAsync(new Proto.Empty())).UserName);

        time.Advance(LoginAddressLimit.Window);
        Assert.Equal("tech", (await auth.LoginAsync(new Proto.LoginRequest { UserName = "tech", Password = "Tech-pass-123" })).User.UserName);
    }

    [Fact]
    public void TheAddressLimitCountsOnlyFailuresPerAddressAndIPv6PerNetwork()
    {
        var time = new FakeTimeProvider();
        var limit = new LoginAddressLimit(time);
        for (var i = 1; i < LoginAddressLimit.MaxFailures; i++)
        {
            Assert.False(limit.RecordFailure("2001:db8:1:2::" + i));
        }

        Assert.False(limit.IsLimited("2001:db8:1:2::1"));
        Assert.True(limit.RecordFailure("2001:db8:1:2::ffff")); // the fiftieth of this network
        Assert.True(limit.IsLimited("2001:db8:1:2::7"));
        Assert.False(limit.RecordFailure("2001:db8:1:2::7")); // said once
        Assert.False(limit.IsLimited("2001:db8:1:3::1"));
        Assert.False(limit.IsLimited("10.0.0.5"));
        Assert.False(limit.IsLimited("local"));
    }

    [Fact]
    public async Task AWrongSetupCodeKeepsOnlyAShortCleanNameAndALongCodeIsWrong()
    {
        await using var host = await TestServerHost.StartAsync(createAdmin: false);
        var manager = host.Get<AuthManager>();
        var code = (await File.ReadAllTextAsync(manager.SetupCodePath)).Trim();

        var wrong = await Assert.ThrowsAsync<AuthException>(() => manager.CreateFirstAdminAsync("boss\nforged line" + new string('a', 200_000), "Boss-pass-123", "AAAA-BBBB-CCCC", isLoopback: false, false, "10.0.0.9", CancellationToken.None));
        Assert.Equal("The setup code is wrong.", wrong.Message);
        var padded = await Assert.ThrowsAsync<AuthException>(() => manager.CreateFirstAdminAsync("boss", "Boss-pass-123", code + new string(' ', 200_000), isLoopback: false, false, "10.0.0.9", CancellationToken.None));
        Assert.Equal("The setup code is wrong.", padded.Message);

        var entries = (await host.Get<AuditLog>().ListAsync(0, 0, CancellationToken.None)).Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.InRange(e.UserName.Length, 1, UserStore.MaxUserNameLength);
            Assert.DoesNotContain(e.UserName, char.IsControl);
        });
    }

    [Fact]
    public void AuditTextsHoldNoControlCharacters()
    {
        Assert.Equal("eve  [INF] forged", AuditLog.Clean(" eve\r\n[INF] forged\n"));
        Assert.Equal("tab here", AuditLog.Clean("tab\there"));
        Assert.Equal(AuditLog.MaxTextLength, AuditLog.Clean(new string('a', 100_000)).Length);
        Assert.Equal("Anna Müller", AuditLog.Clean("Anna Müller"));
    }
}
