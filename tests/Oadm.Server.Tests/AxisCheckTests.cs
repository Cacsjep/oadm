using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// Production hardening 2: the add page sends a credential only to a device that passed the anonymous Axis
/// check, HTTPS first, then HTTP; Basic over plain HTTP only to such a verified device.
/// </summary>
public sealed class AxisCheckTests
{
    private const string Password = "s3cret-Pass";

    [Theory]
    [InlineData((int)FakeAxisAnswer.NotFound)]
    [InlineData((int)FakeAxisAnswer.NoSerial)]
    [InlineData((int)FakeAxisAnswer.NoModel)]
    [InlineData((int)FakeAxisAnswer.OtherSerial)]
    [InlineData((int)FakeAxisAnswer.Unauthorized)]
    public async Task ADeviceThatFailsTheAxisCheckNeverGetsACredential(int answer)
    {
        var network = new FakeAxisNetwork();
        var imitation = network.Add("10.9.1.1", FakeSerials.Make(1), Password);
        imitation.AnonymousAnswer = (FakeAxisAnswer)answer;
        imitation.ServesHttps = true;
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.1.1", "10.9.1.1");

        var device = devices["10.9.1.1"];
        Assert.Equal(Proto.AuthState.Unreachable, device.AuthState);
        Assert.EndsWith("No password was sent.", device.AuthDetail, StringComparison.Ordinal);
        Assert.Empty(imitation.CredentialRequests);
        Assert.DoesNotContain(network.Connections, c => c.Credentials is not null);

        // A typed credential does not pass either.
        var retry = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = device.DiscoveredId, UserName = "root", Password = Password });
        Assert.Equal(Proto.AuthState.Unreachable, retry.AuthState);
        Assert.Empty(imitation.CredentialRequests);
    }

    [Fact]
    public async Task AVerifiedDeviceGetsTheCredentialOverHttpsFirst()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.1.2", FakeSerials.Make(2), Password);
        camera.ServesHttps = true;
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "wrong", Password = "wrong-password" });
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (_, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.1.2", "10.9.1.2");

        Assert.Equal(Proto.AuthState.Authenticated, devices["10.9.1.2"].AuthState);
        Assert.Equal([("https", "wrong"), ("https", "root")], camera.CredentialRequests);

        // The anonymous check on HTTPS comes before the first credential, and once per login run.
        var connections = network.Connections.Where(c => c.Address == "10.9.1.2").ToList();
        var firstCredential = connections.FindIndex(c => c.Credentials is not null);
        Assert.True(firstCredential > 0);
        Assert.Contains(connections.Take(firstCredential), c => c.Credentials is null && c.Scheme == "https");
        Assert.Single(connections, c => c.Credentials is null && c.Scheme == "https");
        Assert.All(connections.Where(c => c.Credentials is not null), c => Assert.Equal("https", c.Scheme));
    }

    [Fact]
    public async Task ADeviceWithoutHttpsGetsTheCredentialOverHttpWithBasicAllowed()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.1.3", FakeSerials.Make(3), Password); // HTTP only
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (_, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.1.3", "10.9.1.3");

        Assert.Equal(Proto.AuthState.Authenticated, devices["10.9.1.3"].AuthState);
        Assert.Equal([("http", "root")], camera.CredentialRequests);
        var connections = network.Connections.Where(c => c.Address == "10.9.1.3").ToList();
        var login = Assert.Single(connections, c => c.Credentials is not null);
        Assert.Equal("http", login.Scheme);
        Assert.True(login.AllowBasicOverHttp);
        var firstCredential = connections.IndexOf(login);
        Assert.Contains(connections.Take(firstCredential), c => c.Credentials is null && c.Scheme == "http");
        Assert.Contains(connections.Take(firstCredential), c => c.Credentials is null && c.Scheme == "https"); // tried first, refused
    }

    [Theory]
    [InlineData("ACCC8E000001", "P3265-V", "ACCC8E000001", true)]
    [InlineData("ac:cc:8e:00:00:01", "P3265-V", "ACCC8E000001", true)]
    [InlineData("ACCC8E000002", "P3265-V", "ACCC8E000001", false)]
    [InlineData("ACCC8E00001", "P3265-V", "ACCC8E000001", false)]
    [InlineData("not-a-serial", "P3265-V", "ACCC8E000001", false)]
    [InlineData("ACCC8E000001", "", "ACCC8E000001", false)]
    [InlineData(null, "P3265-V", "ACCC8E000001", false)]
    public void TheAxisAnswerNeedsTheSerialAndAProductNumber(string? serial, string model, string expected, bool verified)
    {
        var properties = new Dictionary<string, string> { ["ProdNbr"] = model };
        if (serial is not null)
        {
            properties["SerialNumber"] = serial;
        }

        var verdict = DiscoveryAuthenticator.Judge(properties, expected);

        Assert.Equal(verified, verdict.Kind == DiscoveryAuthenticator.AxisVerdictKind.Verified);
    }
}
