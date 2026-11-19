using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The first password from an entry of the credential list (the credential choice of the add page and of Set password): the
/// server reads the stored password itself, Administrators only, root's entries only, checked as a first password.
/// </summary>
public sealed class FirstPasswordCredentialTests
{
    private const string ListPassword = "list-Pass1";

    private static FakeAxisNetwork Network()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.4.1", FakeSerials.Make(41), password: null); // factory default
        network.Add("10.9.4.2", FakeSerials.Make(42), password: null);
        return network;
    }

    private static async Task<string> AddEntryAsync(TestServerHost host, string user = "root", string password = ListPassword) =>
        (await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = user, Password = password })).Id;

    [Fact]
    public async Task TheAddSetsTheStoredPasswordOfTheChosenEntry()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var id = await AddEntryAsync(host);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = id },
        });

        Assert.Single(reply.DeviceIds);
        Assert.Equal(ListPassword, network["10.9.4.1"].Password);
        Assert.Equal(0, network["10.9.4.1"].PasswordInUrl);
        var device = Assert.Single((await host.Devices.ListAsync(new Proto.Empty())).Devices);
        Assert.True(device.HasCredentials); // stored as the device's login
    }

    [Fact]
    public async Task TheAddRefusesAnUnknownEntryAndChangesNothing()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");

        var ex = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = Guid.NewGuid().ToString() },
        }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal(DeviceFirstSetupTexts.CredentialGone, ex.Status.Detail);
        Assert.Null(network["10.9.4.1"].Password);
        Assert.Empty((await host.Devices.ListAsync(new Proto.Empty())).Devices);
    }

    [Fact]
    public async Task TheAddRefusesAnEntryOfAnotherUserAndATypedPasswordWithIt()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var other = await AddEntryAsync(host, "operator", "op-Pass-1");
        var root = await AddEntryAsync(host);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");

        var notRoot = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = other },
        }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, notRoot.StatusCode);
        Assert.Equal(DeviceFirstSetupTexts.RootOnly, notRoot.Status.Detail);

        var both = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = root },
            InitialPasswords = { [FakeSerials.Make(41)] = "typed-Pass1" },
        }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, both.StatusCode);
        Assert.Null(network["10.9.4.1"].Password);
    }

    [Fact]
    public async Task AStoredPasswordThatDoesNotFitAFirstPasswordIsRefusedWithoutAWrite()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);

        // An entry the list's own check would refuse today (an older entry, a restored backup): written directly.
        var id = Guid.NewGuid();
        var factory = host.Get<IDbContextFactory<OadmDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CredentialListEntries.Add(new CredentialListEntry
            {
                Id = id,
                UserName = "root",
                EncryptedPassword = host.Get<CredentialProtector>().Protect("badépassword", id.ToByteArray()),
                CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = id.ToString() },
        }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(FirstPasswordCredential.DoesNotFit, ex.Status.Detail);
        Assert.DoesNotContain("badé", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, network["10.9.4.1"].PwdgrpCalls);
    }

    [Fact]
    public async Task SetPasswordSetsTheStoredPasswordOnManagedDevicesAndAuditsWithoutIt()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var id = await AddEntryAsync(host);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.2");
        var commit = new Proto.CommitRequest { SessionId = session, DiscoveredIds = { FakeSerials.Make(41), FakeSerials.Make(42) } };
        var ids = (await host.AddDevices.CommitAsync(commit)).DeviceIds; // added in factory default, no password yet

        var reply = await host.Devices.SetFirstPasswordAsync(new Proto.SetFirstPasswordRequest { DeviceIds = { ids }, CredentialId = id });

        Assert.All(reply.Results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal(ListPassword, network["10.9.4.1"].Password);
        Assert.Equal(ListPassword, network["10.9.4.2"].Password);
        var audit = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.FirstPasswordSet);
        Assert.Equal("root", audit.Target);
        Assert.Equal("2 devices, 2 set, from the credential list", audit.Detail);
        Assert.DoesNotContain(ListPassword, audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetPasswordRefusesAnUnknownEntryBothAtOnceAndUsersWithoutTheCredentialList()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var id = await AddEntryAsync(host);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");
        var deviceId = Assert.Single((await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { FakeSerials.Make(41) } })).DeviceIds);

        var unknown = await Assert.ThrowsAsync<RpcException>(() => host.Devices.SetFirstPasswordAsync(
            new Proto.SetFirstPasswordRequest { DeviceIds = { deviceId }, CredentialId = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(DeviceFirstSetupTexts.CredentialGone, unknown.Status.Detail);

        var both = await Assert.ThrowsAsync<RpcException>(() => host.Devices.SetFirstPasswordAsync(
            new Proto.SetFirstPasswordRequest { DeviceIds = { deviceId }, CredentialId = id, Password = "typed-Pass1" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, both.StatusCode);
        Assert.Equal(FirstPasswordCredential.Both, both.Status.Detail);

        // An Operator may set a typed first password but never use the credential list (it is Administrator only).
        var operatorDevices = new Proto.DeviceService.DeviceServiceClient(await host.InvokerForUserAsync("op", UserRole.Operator));
        var denied = await Assert.ThrowsAsync<RpcException>(() => operatorDevices.SetFirstPasswordAsync(
            new Proto.SetFirstPasswordRequest { DeviceIds = { deviceId }, CredentialId = id }).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        Assert.Equal(FirstPasswordCredential.AdminOnly, denied.Status.Detail);
        Assert.Null(network["10.9.4.1"].Password);
    }

    [Fact]
    public async Task AnOperatorsAddWithAnEntryIsRefused()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var id = await AddEntryAsync(host);
        var (session, _) = await TestHelpers.ScanAsync(host, "10.9.4.1", "10.9.4.1");
        var invoker = await host.InvokerForUserAsync("op", UserRole.Operator);

        var ex = await Assert.ThrowsAsync<RpcException>(() => new Proto.AddDevicesService.AddDevicesServiceClient(invoker).CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { FakeSerials.Make(41) },
            InitialCredentialIds = { [FakeSerials.Make(41)] = id },
        }).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Null(network["10.9.4.1"].Password);
    }
}
