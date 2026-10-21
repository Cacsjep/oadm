using Oadm.Core.Sites;
using Oadm.Core.Tests.Sites;
using Oadm.Core.Vapix;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Config;

using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// The site link with the real OADM Site Node in this process: a dropped link is a site error and never a device error,
/// a cancelled connect is a cancellation, the port request follows the caller and is asked again when it did not arrive,
/// and the server can ask whether the node answers now (CLAUDE.md "# Sites", Site errors are not device errors).
/// </summary>
public sealed class SiteLinkServerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AConnectionWhoseSiteLinkDropsEndsWithSiteOfflineNotADeviceError()
    {
        await using var device = LoopbackDevice.StartHttp();
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.20", 80, device.EndPoint);
        var id = await server.BindAsync(node);
        Assert.True(await server.Manager.LinkAnswersAsync(id, CancellationToken.None));
        Assert.False(await server.Manager.LinkAnswersAsync(Guid.NewGuid(), CancellationToken.None));

        await using var stream = await server.Manager.OpenStreamAsync(id, "192.168.0.20", 80, SiteDialKind.Short, Generous, CancellationToken.None);
        await server.Manager.DropSessionAsync(id);

        var error = await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[16]));
        Assert.Equal(SiteProblem.Offline, SiteErrors.Find(error)?.Problem);
        Assert.Equal("Site offline: Building B", error.Message);
        Assert.Equal(SdkStatus.SiteOffline, DeviceStatusClassifier.FromException(new HttpRequestException("read failed", error)));
    }

    [Fact]
    public async Task ACancelledConnectIsACancellationNeverARawRpcError()
    {
        await using var device = LoopbackDevice.StartHttp();
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.20", 80, device.EndPoint);
        var id = await server.BindAsync(node);
        await (await server.Manager.OpenStreamAsync(id, "192.168.0.20", 80, SiteDialKind.Short, Generous, CancellationToken.None)).DisposeAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await server.Manager.OpenStreamAsync(id, "192.168.0.20", 80, SiteDialKind.Permanent, Generous, cancelled.Token));
    }

    [Fact]
    public async Task APortRequestThatDidNotArriveIsAskedAgain()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);

        // The caller gave up at once (a poll's timeout): the request did not reach the node, nothing was waited for.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var error = await Assert.ThrowsAsync<SiteException>(async () =>
            await server.Manager.OpenStreamAsync(id, "192.168.0.30", 8443, SiteDialKind.Short, Generous, cancelled.Token));
        Assert.Equal(SiteProblem.PortNotAllowed, error.Problem);
        Assert.DoesNotContain(node.Get<NodeStore>().Snapshot().Requested, p => p.Port == 8443);

        // The next dial asks again.
        await Assert.ThrowsAsync<SiteException>(async () =>
            await server.Manager.OpenStreamAsync(id, "192.168.0.30", 8443, SiteDialKind.Short, Generous, CancellationToken.None));
        Assert.Single(node.Get<NodeStore>().Snapshot().Requested, p => p.Port == 8443);
    }
}
