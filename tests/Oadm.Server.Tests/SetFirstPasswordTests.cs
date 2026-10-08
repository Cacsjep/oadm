using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>DeviceService.SetFirstPassword / GetPassphrasePolicies: "Set password" for managed factory-default devices.</summary>
public sealed class SetFirstPasswordTests
{
    private const string NewPassword = "Fresh-Pass-2026";

    private static Proto.SetFirstPasswordRequest Request(string password, params Device[] devices)
    {
        var request = new Proto.SetFirstPasswordRequest { Password = password };
        request.DeviceIds.AddRange(devices.Select(d => d.Id.ToString()));
        return request;
    }

    private static Task<Device> AddAsync(TestServerHost host, string address, int serial, DeviceScheme scheme = DeviceScheme.Http) =>
        host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = scheme, Status = SdkDeviceStatus.PasswordNotSet },
            CancellationToken.None);

    [Fact]
    public async Task TheFirstPasswordIsSetInThePostBodyStoredAndAuditedWithoutThePassword()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.3.1", FakeSerials.Make(1), password: null);
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddAsync(host, "10.9.3.1", 1);

        var reply = await host.Devices.SetFirstPasswordAsync(Request(NewPassword, device));

        var result = Assert.Single(reply.Results);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(NewPassword, camera.Password);
        Assert.Equal("root", camera.User);
        var request = Assert.Single(camera.PwdgrpRequests);
        Assert.Equal(("http", "POST", false), request); // the device offers no HTTPS: plain HTTP, body only
        Assert.Equal(0, camera.PasswordInUrl);
        var stored = await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(("root", NewPassword), (stored!.UserName, stored.Password));
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).Status == Proto.DeviceStatus.Ok,
            "refresh after the first password");

        var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.FirstPasswordSet);
        Assert.Equal("root", entry.Target);
        Assert.Equal("1 device, 1 set", entry.Detail);
        Assert.DoesNotContain(NewPassword, entry.Detail + entry.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpsIsUsedWhenTheDeviceOffersIt()
    {
        var network = new FakeAxisNetwork();
        var stillHttp = network.Add("10.9.3.2", FakeSerials.Make(2), password: null);
        stillHttp.ServesHttps = true;
        var pinned = network.Add("10.9.3.3", FakeSerials.Make(3), password: null);
        pinned.ServesHttps = true;
        await using var host = await TestServerHost.StartAsync(network);
        var a = await AddAsync(host, "10.9.3.2", 2);
        var b = await AddAsync(host, "10.9.3.3", 3, DeviceScheme.Https);

        var reply = await host.Devices.SetFirstPasswordAsync(Request(NewPassword, a, b));

        Assert.All(reply.Results, r => Assert.True(r.Ok, r.Message));
        Assert.Equal(("https", "POST", false), Assert.Single(stillHttp.PwdgrpRequests));
        Assert.Equal(("https", "POST", false), Assert.Single(pinned.PwdgrpRequests));
    }

    [Fact]
    public async Task ADeviceThatAlreadyHasAPasswordIsNotTouched()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.3.4", FakeSerials.Make(4), "existing-pw");
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddAsync(host, "10.9.3.4", 4);

        var reply = await host.Devices.SetFirstPasswordAsync(Request(NewPassword, device));

        var result = Assert.Single(reply.Results);
        Assert.False(result.Ok);
        Assert.Equal("The device already has a password. Nothing was changed.", result.Message);
        Assert.Equal(0, camera.PwdgrpCalls);
        Assert.Equal("existing-pw", camera.Password);
        Assert.Null(await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task APasswordTheDevicePolicyRefusesIsReportedAndNotStored()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.3.5", FakeSerials.Make(5), password: null);
        camera.PassphrasePolicy = "length";
        camera.MinimumPasswordLength = 15;
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddAsync(host, "10.9.3.5", 5);

        var policies = await host.Devices.GetPassphrasePoliciesAsync(new Proto.DeviceIds { Ids = { device.Id.ToString() } });
        Assert.Equal("length", Assert.Single(policies.Policies).Policy);

        var reply = await host.Devices.SetFirstPasswordAsync(Request("Short-1", device));

        var result = Assert.Single(reply.Results);
        Assert.False(result.Ok);
        Assert.Equal("The device rejected the password: Error: the password does not meet the passphrase policy.", result.Message);
        Assert.Null(camera.Password);
        Assert.Null(await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None));
        Assert.Equal(SdkDeviceStatus.PasswordNotSet, (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task AnotherDeviceAtTheAddressAndUnreachableDevicesGetNothing()
    {
        var network = new FakeAxisNetwork();
        var other = network.Add("10.9.3.6", FakeSerials.Make(66), password: null);
        await using var host = await TestServerHost.StartAsync(network);
        var moved = await AddAsync(host, "10.9.3.6", 6);
        var gone = await AddAsync(host, "10.9.3.7", 7);

        var reply = await host.Devices.SetFirstPasswordAsync(Request(NewPassword, moved, gone));

        Assert.Equal("Another device answers at this address. Nothing was changed.", reply.Results[0].Message);
        Assert.StartsWith("Unreachable - ", reply.Results[1].Message, StringComparison.Ordinal);
        Assert.Equal(0, other.PwdgrpCalls);
    }

    [Fact]
    public async Task InvalidPasswordsAndEmptySelectionsAreRefused()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await AddAsync(host, "10.9.3.8", 8);

        var empty = await Assert.ThrowsAsync<RpcException>(async () => await host.Devices.SetFirstPasswordAsync(Request("", device)));
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        Assert.Equal("Password must be 1-64 printable ASCII characters.", empty.Status.Detail);
        var none = await Assert.ThrowsAsync<RpcException>(async () => await host.Devices.SetFirstPasswordAsync(Request(NewPassword)));
        Assert.Equal(StatusCode.InvalidArgument, none.StatusCode);
    }

    [Fact]
    public async Task OperatorsMaySetFirstPasswordsAndAnonymousCallersMayNot()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.3.9", FakeSerials.Make(9), password: null);
        await using var host = await TestServerHost.StartAsync(network);
        var device = await AddAsync(host, "10.9.3.9", 9);
        Assert.Equal(Access.Operator, AccessPolicy.For("/oadm.v1.DeviceService/SetFirstPassword"));
        Assert.Equal(Access.Operator, AccessPolicy.For("/oadm.v1.DeviceService/GetPassphrasePolicies"));

        var anonymous = new Proto.DeviceService.DeviceServiceClient(host.InvokerFor(null));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await anonymous.SetFirstPasswordAsync(Request(NewPassword, device)));
        Assert.Equal(StatusCode.Unauthenticated, denied.StatusCode);

        var operatorClient = new Proto.DeviceService.DeviceServiceClient(await host.InvokerForUserAsync("tech1", Oadm.Sdk.Plugins.UserRole.Operator));
        var reply = await operatorClient.SetFirstPasswordAsync(Request(NewPassword, device));
        Assert.True(Assert.Single(reply.Results).Ok);
        var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.FirstPasswordSet);
        Assert.Equal("tech1", entry.UserName);
    }
}
