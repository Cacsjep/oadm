using Grpc.Core;

using Oadm.Sdk.Devices;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// "Add devices", automatic login and the cameras' brute force protection (AXIS OS default: blocked after 5 failed logins in
/// 30 s): at most 4 credentials per device, the add page's choice of them, and a block is said as such and ends the tries.
/// </summary>
public sealed class BruteForceTests
{
    private const string Password = "s3cret-Pass";

    [Fact]
    public async Task AtMostFourCredentialsAreTriedSoTheCameraNeverBlocks()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.2.1", FakeSerials.Make(21), Password);
        camera.BlockAfterRejected = 5;
        await using var host = await TestServerHost.StartAsync(network);
        for (var i = 1; i <= 5; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "wrong-password-" + i });
        }

        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (_, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.2.1", "10.9.2.1");

        var device = devices["10.9.2.1"];
        Assert.Equal(Proto.AuthState.LoginFailed, device.AuthState);
        Assert.Equal("None of the 4 known credentials worked.", device.AuthDetail);
        Assert.Equal(["user1", "user2", "user3", "user4"], camera.CredentialRequests.Select(r => r.User));
        Assert.Equal(4, camera.RejectedLogins); // below the camera's limit
    }

    [Fact]
    public async Task TheChosenCredentialsAreTriedInTheirOrderAndNoOthers()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.2.2", FakeSerials.Make(22), Password);
        await using var host = await TestServerHost.StartAsync(network);
        var wrong = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "first", Password = "wrong-password-1" });
        for (var i = 2; i <= 5; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "wrong-password-" + i });
        }

        var right = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var session = await host.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest
        {
            From = "10.9.2.2",
            To = "10.9.2.2",
            CredentialIds = { wrong.Id, right.Id },
        });
        var devices = await TestHelpers.WatchToEndAsync(host, session.SessionId);

        Assert.Equal(Proto.AuthState.Authenticated, Assert.Single(devices.Values).AuthState);
        Assert.Equal(["first", "root"], camera.CredentialRequests.Select(r => r.User));
    }

    [Fact]
    public async Task MoreThanFourChosenCredentialsAreRefused()
    {
        await using var host = await TestServerHost.StartAsync(new FakeAxisNetwork());
        var request = new Proto.ZeroConfRequest();
        request.CredentialIds.AddRange(Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString()));

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.Discovery.StartZeroConfAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(DiscoveryAuthenticator.TooManyChosenText, ex.Status.Detail);
    }

    [Fact]
    public async Task ABlockedCameraIsSaidToBlockAndGetsNoPassword()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.2.3", FakeSerials.Make(23), Password);
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.2.3", "10.9.2.3");
        Assert.Equal(Proto.AuthState.Authenticated, devices["10.9.2.3"].AuthState);

        // Blocked meanwhile (other failed logins of this address): the typed retry finds the block in the Axis check.
        camera.BlockAfterRejected = 0;
        var before = camera.CredentialRequests.Count;
        var retry = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = session,
            DiscoveredId = devices["10.9.2.3"].DiscoveredId,
            UserName = "root",
            Password = Password,
        });

        Assert.Equal(Proto.AuthState.LoginFailed, retry.AuthState);
        Assert.Equal(DeviceMessages.BruteForceBlocked, retry.AuthDetail);
        Assert.Equal(before, camera.CredentialRequests.Count);
    }

    [Fact]
    public async Task ACameraThatBlocksDuringTheTriesEndsThem()
    {
        // A stricter setting on the camera (blocked after 2 failures): the third credential gets the block, no fourth is sent.
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.2.4", FakeSerials.Make(24), Password);
        camera.BlockAfterRejected = 2;
        await using var host = await TestServerHost.StartAsync(network);
        for (var i = 1; i <= 4; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "wrong-password-" + i });
        }

        var (_, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.2.4", "10.9.2.4");

        var device = devices["10.9.2.4"];
        Assert.Equal(Proto.AuthState.LoginFailed, device.AuthState);
        Assert.Equal(DeviceMessages.BruteForceBlocked, device.AuthDetail);
        Assert.Equal(["user1", "user2", "user3"], camera.CredentialRequests.Select(r => r.User));
    }
}
