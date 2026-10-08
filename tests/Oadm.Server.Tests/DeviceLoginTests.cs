using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Perf;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>DeviceService.LogIn: "Log in" for managed devices whose stored credentials are rejected.</summary>
public sealed class DeviceLoginTests
{
    private static Proto.DeviceLogInRequest Request(string user, string password, bool save, params Device[] devices)
    {
        var request = new Proto.DeviceLogInRequest { UserName = user, Password = password, SaveToCredentialList = save };
        request.DeviceIds.AddRange(devices.Select(d => d.Id.ToString()));
        return request;
    }

    [Fact]
    public async Task EveryDeviceGetsItsOwnResultAndOnlyAcceptedLoginsAreStored()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "Right-pass1");
        network.Add("10.9.0.2", FakeSerials.Make(2), "Another-pass");
        network.Add("10.9.0.4", FakeSerials.Make(99), "Right-pass1");
        network.Add("10.9.0.5", FakeSerials.Make(5), "Right-pass1");
        await using var host = await TestServerHost.StartAsync(network);
        var ok = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1, "old", SdkDeviceStatus.CredentialsRequired);
        var wrong = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, "old", SdkDeviceStatus.CredentialsRequired);
        var gone = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.3", 3, "old", SdkDeviceStatus.CredentialsRequired);
        var other = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.4", 4, "old", SdkDeviceStatus.CredentialsRequired);
        var pinned = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.5", 5, "old", SdkDeviceStatus.CertificateChanged);

        var reply = await host.Devices.LogInAsync(Request("root", "Right-pass1", false, ok, wrong, gone, other, pinned));

        Assert.Equal([ok.Id.ToString(), wrong.Id.ToString(), gone.Id.ToString(), other.Id.ToString(), pinned.Id.ToString()], reply.Results.Select(r => r.DeviceId));
        Assert.True(reply.Results[0].Ok);
        Assert.Equal("", reply.Results[0].Message);
        Assert.False(reply.Results[1].Ok);
        Assert.True(reply.Results[1].Rejected);
        Assert.Equal("The user name or password is wrong.", reply.Results[1].Message);
        Assert.False(reply.Results[2].Rejected);
        Assert.StartsWith("Unreachable - ", reply.Results[2].Message, StringComparison.Ordinal);
        Assert.Equal("Another device answers at this address.", reply.Results[3].Message);
        Assert.Equal("Certificate changed. Remove the device and add it again to trust the new certificate.", reply.Results[4].Message);
        Assert.Equal("", reply.CredentialListNote);

        // Stored only for the device that accepted it; the refresh brings it back to Ok and the polling uses it.
        var store = host.Get<CredentialStore>();
        Assert.Equal("Right-pass1", (await store.GetAsync(ok.Id, CancellationToken.None))!.Password);
        foreach (var device in new[] { wrong, gone, other, pinned })
        {
            Assert.Equal("old", (await store.GetAsync(device.Id, CancellationToken.None))!.Password);
        }

        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, ok.Id.ToString())).Status == Proto.DeviceStatus.Ok,
            "refresh after the login");
        var polled = await host.Get<DevicePollingService>().PollAsync(ok.Id, CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.Ok, polled!.Status);
        Assert.Equal(SdkDeviceStatus.CredentialsRequired, (await host.Get<DevicePollingService>().PollAsync(wrong.Id, CancellationToken.None))!.Status);

        // Audit without the password.
        var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.DeviceLogin);
        Assert.Equal("root", entry.Target);
        Assert.Equal("5 devices, 1 logged in", entry.Detail);
        Assert.DoesNotContain("Right-pass1", entry.Detail + entry.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceWithAChangedCertificateIsNotContacted()
    {
        var network = new FakeAxisNetwork();
        var fake = network.Add("10.9.0.5", FakeSerials.Make(5), "Right-pass1");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.5", 5, password: null, SdkDeviceStatus.CertificateChanged);

        var reply = await host.Devices.LogInAsync(Request("root", "Right-pass1", false, device));

        Assert.False(reply.Results[0].Ok);
        Assert.Empty(fake.CredentialRequests);
        Assert.Null(await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task OnlyAdministratorsSaveTheLoginInTheCredentialList()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "Right-pass1");
        network.Add("10.9.0.2", FakeSerials.Make(2), "Right-pass2");
        await using var host = await TestServerHost.StartAsync(network);
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1, "old", SdkDeviceStatus.CredentialsRequired);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, "old", SdkDeviceStatus.CredentialsRequired);
        var op = new Proto.DeviceService.DeviceServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));

        // An operator may log in, but the credential list (Admin only) stays as it is.
        var asOperator = await op.LogInAsync(Request("root", "Right-pass1", true, a));
        Assert.True(asOperator.Results[0].Ok);
        Assert.Empty((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);

        // Nothing is saved when no device accepted the login.
        var rejected = await host.Devices.LogInAsync(Request("root", "Wrong-pass", true, b));
        Assert.False(rejected.Results[0].Ok);
        Assert.Empty((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);

        var asAdmin = await host.Devices.LogInAsync(Request("root", "Right-pass2", true, b));
        Assert.True(asAdmin.Results[0].Ok);
        var saved = Assert.Single((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);
        Assert.Equal("root", saved.UserName);

        // The same pair again is the existing entry.
        await host.Devices.LogInAsync(Request("root", "Right-pass2", true, b));
        Assert.Single((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);
    }

    [Fact]
    public async Task AFullCredentialListIsANoteNotAFailedLogin()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "Right-pass1");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1, "old", SdkDeviceStatus.CredentialsRequired);
        for (var i = 0; i < CredentialListStore.MaxEntries; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "Pass-word-" + i });
        }

        var reply = await host.Devices.LogInAsync(Request("root", "Right-pass1", true, device));

        Assert.True(reply.Results[0].Ok);
        Assert.Equal("The credential list is full (20 entries); the login was not saved there.", reply.CredentialListNote);
        Assert.Equal("Right-pass1", (await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
    }

    [Fact]
    public async Task OperatorsMayLogInAnonymousCallersMayNot()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1, "old", SdkDeviceStatus.CredentialsRequired);
        var anonymous = new Proto.DeviceService.DeviceServiceClient(host.InvokerFor(null));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await anonymous.LogInAsync(Request("root", "x", false, device)));
        Assert.Equal(StatusCode.Unauthenticated, denied.StatusCode);

        var op = new Proto.DeviceService.DeviceServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));
        var reply = await op.LogInAsync(Request("root", "x", false, device));
        Assert.False(reply.Results[0].Ok); // nobody answers at that address
        Assert.Equal("root", (await op.GetCredentialUserNameAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } })).UserName);
    }

    [Fact]
    public async Task MissingUserNamePasswordOrDevicesAreRefused()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        foreach (var request in new[] { Request("", "x", false, device), Request("root", "", false, device), Request("root", "x", false) })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.Devices.LogInAsync(request));
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }
    }

    [Fact]
    public async Task TheSharedStoredUserNamePrefillsTheDialog()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        var c = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.3", 3, password: null);
        await host.Get<CredentialStore>().SetAsync(b.Id, "service", "pw", CancellationToken.None);

        async Task<string> NameOf(params Device[] devices)
        {
            var ids = new Proto.DeviceIds();
            ids.Ids.AddRange(devices.Select(d => d.Id.ToString()));
            return (await host.Devices.GetCredentialUserNameAsync(ids)).UserName;
        }

        Assert.Equal("root", await NameOf(a));
        Assert.Equal("service", await NameOf(b));
        Assert.Equal("", await NameOf(a, b));
        Assert.Equal("", await NameOf(a, c));
        Assert.Equal("", await NameOf(c));
    }

    [Fact]
    public async Task DevicesAreTriedWithBoundedParallelism()
    {
        var network = new FakeAxisNetwork { Latency = TimeSpan.FromMilliseconds(40) };
        await using var host = await TestServerHost.StartAsync(network);
        var devices = new List<Device>();
        for (var i = 1; i <= 40; i++)
        {
            network.Add($"10.9.1.{i}", FakeSerials.Make(i), "Right-pass1");
            devices.Add(await DeviceServiceTests.AddDeviceAsync(host, $"10.9.1.{i}", i, "old", SdkDeviceStatus.CredentialsRequired));
        }

        var reply = await host.Devices.LogInAsync(Request("root", "Right-pass1", false, [.. devices]));

        Assert.All(reply.Results, r => Assert.True(r.Ok, r.Message));
        var service = host.Get<DeviceLoginService>();
        Assert.InRange(service.PeakParallelism, 2, service.MaxParallelism);
    }
}

/// <summary>One LogIn call for 5,000 devices.</summary>
[Trait("Category", "Perf")]
public sealed class DeviceLoginScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LogInOn5000DevicesInOneCall()
    {
        var network = new FakeAxisNetwork();
        for (var i = 0; i < ServerScale.Devices; i++)
        {
            network.Add($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", $"ACCC8E{i:X6}", "Right-pass1");
        }

        await using var host = await TestServerHost.StartAsync(network);
        var ids = await ServerScale.SeedDevicesAsync(host);
        var request = new Proto.DeviceLogInRequest { UserName = "root", Password = "Right-pass1" };
        request.DeviceIds.AddRange(ids.Select(i => i.ToString()));

        Proto.DeviceLogInReply reply = null!;
        await ServerScale.MeasureAsync(output, "LogIn on 5,000 devices (8 at a time, one store transaction)", TimeSpan.FromSeconds(120), async () =>
            reply = await host.Devices.LogInAsync(request));

        Assert.Equal(ServerScale.Devices, reply.Results.Count);
        Assert.All(reply.Results, r => Assert.True(r.Ok, r.Message));
        Assert.InRange(host.Get<DeviceLoginService>().PeakParallelism, 1, 8);
        Assert.Equal(ServerScale.Devices, (await host.Get<CredentialStore>().ListDeviceIdsWithCredentialsAsync(CancellationToken.None)).Count);
    }
}
