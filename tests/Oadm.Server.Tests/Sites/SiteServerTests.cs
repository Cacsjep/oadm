using System.Net;
using System.Net.Sockets;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;
using Oadm.Core.Tests.Sites;
using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.Server.Tests.Support;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Tests.Support;
using Oadm.SiteNode.State;
using Oadm.Contracts.Sites;

using Proto = Oadm.Contracts.V1;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// The server side of the sites with the real OADM Site Node in this process (docs/spec/sites.md, Server side;
/// src/Oadm.SiteNode/README.md, "Tests"), over real TLS and gRPC on loopback.
/// </summary>
public sealed class SiteServerTests
{
    [Fact]
    public async Task AcceptSiteNodesOpensAndClosesTheListenerWithoutARestart()
    {
        await using var server = await SiteServer.StartAsync(accept: false);
        Assert.Equal(0, server.Manager.ListenPort);
        Assert.False((await server.ListAsync()).Accept);

        var on = await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = true });
        Assert.True(on.Accept);
        Assert.NotEqual(0, server.Manager.ListenPort);
        var port = server.Manager.ListenPort;

        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var waiting = await server.WaitingAsync();
        Assert.Equal("site-pc", waiting.Name);
        Assert.Equal(Proto.SiteState.Waiting, waiting.State);
        Assert.Equal("3.7.0-test", waiting.Version);

        var off = await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = false });
        Assert.False(off.Accept);
        Assert.DoesNotContain(off.Sites, s => s.State == Proto.SiteState.Waiting);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, port));
        Assert.Contains(SiteAudit.AcceptOn, await server.AuditActionsAsync());
        Assert.Contains(SiteAudit.AcceptOff, await server.AuditActionsAsync());
    }

    [Fact]
    public async Task TheRightCodeBindsTheNodeAndTheSiteIsOnline()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);

        var id = await server.BindAsync(node, "Building B");

        var site = (await server.ListAsync()).Sites.Single(s => s.Id == id.ToString());
        Assert.Equal("Building B", site.Name);
        Assert.Equal(Proto.SiteState.Online, site.State);
        Assert.Equal(node.Get<SiteNode.Identity.NodeIdentity>().FingerprintHex, site.NodeFingerprint);
        Assert.Equal(NodeBinding.Bound, node.Get<BindingState>().Binding);
        Assert.Equal("Building B", node.Get<BindingState>().Bound!.SiteName);
        Assert.Equal(server.Host.Get<SiteIdentityStore>().FingerprintHex, node.Get<BindingState>().Bound!.Fingerprint);
        Assert.Contains(SiteAudit.Bound, await server.AuditActionsAsync());
        Assert.Equal("Building B", server.Manager.NameOf(id));

        // The node learns the server's addresses to fall back to.
        await Eventually.TrueAsync(() => node.Get<NodeStore>().Snapshot().ServerAddresses.Port > 0, "the server addresses on the node");
    }

    [Fact]
    public async Task AWrongCodeIsRefusedBelowTheFieldAndAudited()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var waiting = await server.WaitingAsync();
        var wrong = node.Code == "ABCDEFGH" ? "ABCDEFGJ" : "ABCDEFGH";

        var error = await SiteAsserts.RefusedAsync(StatusCode.InvalidArgument, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, Name = "Building B", PairingCode = wrong }));

        Assert.Equal(SiteTexts.WrongCode, error.Status.Detail);
        Assert.Contains(SiteAudit.WrongCode, await server.AuditActionsAsync());
        Assert.NotEqual(NodeBinding.Bound, node.Get<BindingState>().Binding);
        Assert.Empty(server.Manager.Sites);

        // A code that is not 8 characters never reaches the node.
        var format = await SiteAsserts.RefusedAsync(StatusCode.InvalidArgument, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, Name = "Building B", PairingCode = "12" }));
        Assert.Equal(SiteTexts.CodeFormat, format.Status.Detail);
    }

    [Fact]
    public async Task FiveWrongCodesAreTooManyAndTheNamesAreUnique()
    {
        await using var server = await SiteServer.StartAsync();
        await using var first = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        await server.BindAsync(first, "Building B");
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-2");
        var waiting = await server.WaitingAsync(node.NodeId);

        var taken = await SiteAsserts.RefusedAsync(StatusCode.AlreadyExists, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, Name = "building b", PairingCode = node.Code }));
        Assert.Equal(SiteTexts.NameTaken, taken.Status.Detail);

        RpcException? last = null;
        for (var i = 0; i < 5; i++)
        {
            var wrong = node.Code == "ABCDEFGH" ? "ABCDEFGJ" : "ABCDEFGH";
            last = await Assert.ThrowsAsync<RpcException>(async () =>
                await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, Name = "Warehouse", PairingCode = wrong }));
        }

        Assert.Equal(StatusCode.ResourceExhausted, last!.StatusCode);
        Assert.Contains("Too many wrong pairing codes", last.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyOfABoundNodeIsRefusedWithItsName()
    {
        await using var server = await SiteServer.StartAsync();
        // Not connected yet: the copy is made before the original is bound (a cloned VM of a fresh install).
        await using var original = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var copyFolder = original.CopyFolder();
        try
        {
            await server.BindAsync(original, "Building B");
            await using var copy = await SiteTestNode.StartAsync(folder: copyFolder);
            Assert.Equal(original.NodeId, copy.NodeId);

            var row = await server.WaitingAsync(copy.NodeId);
            Assert.Equal(SiteTexts.CopyOf("Building B"), row.Message);
            var error = await SiteAsserts.RefusedAsync(StatusCode.AlreadyExists, async () =>
                await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = copy.NodeId, Name = "Copy", PairingCode = copy.Code }));
            Assert.Equal(SiteTexts.CopyOf("Building B"), error.Status.Detail);
            Assert.Single(server.Manager.Sites);
        }
        finally
        {
            SiteTestNode.TryDelete(copyFolder);
        }
    }

    [Fact]
    public async Task OneSessionPerIdentityASecondNodeWithTheSameKeyIsRefused()
    {
        await using var server = await SiteServer.StartAsync();
        await using var original = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(original);
        var copyFolder = original.CopyFolder();
        try
        {
            // A copy made after the bind: bound to the same server with the same key.
            await using var copy = await SiteTestNode.StartAsync(folder: copyFolder);
            await Eventually.TrueAsync(() => server.Manager.SessionsRefused >= 2, "the copy refused (it retries)");

            Assert.Equal(1, server.Manager.SessionsStarted);
            Assert.True(server.Manager.IsOnline(id));
        }
        finally
        {
            SiteTestNode.TryDelete(copyFolder);
        }
    }

    [Fact]
    public async Task RemoveSiteUnbindsTheNodeAndRemovesOrKeepsItsDevices()
    {
        await using var server = await SiteServer.StartAsync();
        await using var kept = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var keptId = await server.BindAsync(kept, "Building B");
        await using var gone = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-2");
        var goneId = await server.BindAsync(gone, "Warehouse");
        var repository = server.Host.Get<DeviceRepository>();
        var keptDevice = await repository.AddAsync(new Device { Serial = "B8A44F000001", Address = "192.168.0.20", SiteId = keptId, Status = SdkStatus.Ok }, CancellationToken.None);
        var goneDevice = await repository.AddAsync(new Device { Serial = "B8A44F000002", Address = "192.168.0.20", SiteId = goneId, Status = SdkStatus.Ok }, CancellationToken.None);

        var keep = await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = keptId.ToString(), KeepDevices = true });
        var remove = await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = goneId.ToString(), KeepDevices = false });

        Assert.True(keep.NodeAnswered);
        Assert.Equal(0, keep.DevicesRemoved);
        Assert.True(remove.NodeAnswered);
        Assert.Equal(1, remove.DevicesRemoved);
        Assert.Equal(SdkStatus.SiteOffline, (await repository.GetAsync(keptDevice.Id, CancellationToken.None))!.Status);
        Assert.Null(await repository.GetAsync(goneDevice.Id, CancellationToken.None));
        Assert.Empty(server.Manager.Sites);
        // Unbind reached both nodes: they open binding again and connect as waiting nodes.
        await Eventually.TrueAsync(() => kept.Get<BindingState>().Binding == NodeBinding.Open, "the kept node unbound");
        await Eventually.TrueAsync(() => gone.Get<BindingState>().Binding == NodeBinding.Open, "the other node unbound");
        await server.WaitingAsync(kept.NodeId);
        Assert.Equal(2, (await server.AuditActionsAsync()).Count(a => a == SiteAudit.Removed));
    }

    [Fact]
    public async Task RemoveForgetsANodeThatDoesNotAnswer()
    {
        await using var server = await SiteServer.StartAsync();
        var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        await node.DisposeAsync();

        var reply = await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = true });

        Assert.False(reply.NodeAnswered);
        Assert.Empty(server.Manager.Sites);
    }

    [Fact]
    public async Task ResetInTheNodeAppTellsTheServerAndTheSiteIsNotBound()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node, "Building B");
        var repository = server.Host.Get<DeviceRepository>();
        var device = await repository.AddAsync(new Device { Serial = "B8A44F000001", Address = "192.168.0.20", SiteId = id, Status = SdkStatus.Ok }, CancellationToken.None);
        // The server's Watch of the node is open once the node's networks arrived.
        await Eventually.TrueAsync(() => server.Host.Get<ISiteNetwork>().Reaches(id, IPAddress.Parse("192.168.0.20")), "the node's networks from Watch");

        var reply = await node.Get<Oadm.SiteNode.AdminChannel.AdminHandler>().HandleAsync(AdminRequest.Of(AdminCommands.Reset), isAdmin: true, CancellationToken.None);

        Assert.True(reply.Ok);
        // The node waited for the server: the site is not bound by the time Reset returns.
        var site = Assert.Single((await server.ListAsync()).Sites, s => s.Id == id.ToString());
        Assert.Equal(Proto.SiteState.NotBound, site.State);
        Assert.Equal(SiteTexts.NotBound, site.Message);
        Assert.Equal(SdkStatus.SiteOffline, (await repository.GetAsync(device.Id, CancellationToken.None))!.Status);
        await Eventually.TrueAsync(async () => (await server.AuditActionsAsync()).Contains(SiteAudit.NodeReset), "the reset audited");
        // The node comes back with its new identity and waits to be bound.
        await server.WaitingAsync(node.NodeId);
    }

    [Fact]
    public async Task RenameReachesTheNode()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node, "Building B");

        var renamed = await server.Client.RenameSiteAsync(new Proto.RenameSiteRequest { Id = id.ToString(), Name = "Building C" });

        Assert.Equal("Building C", renamed.Name);
        Assert.Equal("Building C", node.Get<BindingState>().Bound!.SiteName);
        Assert.Contains(SiteAudit.Renamed, await server.AuditActionsAsync());
        await SiteAsserts.RefusedAsync(StatusCode.InvalidArgument, async () =>
            await server.Client.RenameSiteAsync(new Proto.RenameSiteRequest { Id = id.ToString(), Name = "  " }));
    }

    [Fact]
    public async Task VapixGoesThroughTheSiteWithTlsAndThePinEndToEnd()
    {
        await using var device = LoopbackDevice.StartHttps();
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.20", 443, device.EndPoint);
        var id = await server.BindAsync(node);
        var router = server.Host.Get<DeviceRouter>();
        var options = new VapixConnectionOptions
        {
            Address = "192.168.0.20",
            Scheme = "https",
            PinnedCertificateFingerprint = device.Fingerprint,
            Timeout = TimeSpan.FromSeconds(30),
            Route = DeviceRoute.ViaSite(id),
        };

        using var client = VapixClient.Create(options, router);
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/axis-cgi/anything.cgi"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(device.Fingerprint, client.CertificateFingerprint);

        // Another certificate pinned: refused by the server's TLS, end to end through the node.
        using var wrongPin = VapixClient.Create(options with { PinnedCertificateFingerprint = new string('A', 64) }, router);
        await Assert.ThrowsAsync<CertificateChangedException>(async () =>
            await wrongPin.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/axis-cgi/anything.cgi"), CancellationToken.None));
    }

    [Fact]
    public async Task ADeadDeviceIsASocketErrorAndAnAddressOutsideTheNetworksASiteError()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Refuse("192.168.0.21", 80);
        var id = await server.BindAsync(node);

        var refused = await Assert.ThrowsAsync<SocketException>(async () =>
            await server.Manager.OpenStreamAsync(id, "192.168.0.21", 80, SiteDialKind.Short, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);

        var outside = await Assert.ThrowsAsync<SiteException>(async () =>
            await server.Manager.OpenStreamAsync(id, "10.99.0.1", 80, SiteDialKind.Short, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(SiteProblem.OutsideNetworks, outside.Problem);
    }

    [Fact]
    public async Task APortNotAllowedIsNotDialedAndAskedForOnce()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);

        for (var i = 0; i < 3; i++)
        {
            var error = await Assert.ThrowsAsync<SiteException>(async () =>
                await server.Manager.OpenStreamAsync(id, "192.168.0.30", 8443, SiteDialKind.Short, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(SiteProblem.PortNotAllowed, error.Problem);
            Assert.Equal("Port 8443 is not allowed at Building B. Allow it in the OADM Site Node app.", error.Message);
        }

        var requested = node.Get<NodeStore>().Snapshot().Requested;
        Assert.Single(requested, p => p.Port == 8443);
        await Eventually.TrueAsync(async () => (await server.ListAsync()).Sites.Single(s => s.Id == id.ToString()).PortsWaiting == 1, "1 port waiting on the Sites page");
    }

    [Fact]
    public async Task AtMostTwoLargeTransfersPerSiteTheThirdWaits()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        var router = server.Host.Get<DeviceRouter>();
        var route = DeviceRoute.ViaSite(id);

        var first = await router.EnterLargeTransferAsync(route, CancellationToken.None);
        var second = await router.EnterLargeTransferAsync(route, CancellationToken.None);
        var third = router.EnterLargeTransferAsync(route, CancellationToken.None).AsTask();

        Assert.False(third.IsCompleted);
        Assert.Equal(2, server.Manager.LargeTransfersRunning(id));
        Assert.Null(await router.EnterLargeTransferAsync(DeviceRoute.Local, CancellationToken.None));
        first!.Dispose();
        (await third)!.Dispose();
        second!.Dispose();
        Assert.Equal(0, server.Manager.LargeTransfersRunning(id));
    }

    [Fact]
    public async Task TimeoutsThroughTheSiteGetTheMeasuredRoundTripFourTimes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await SiteServer.StartAsync(time: time);
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);

        await Eventually.TrueAsync(() =>
        {
            if (server.Manager.AnswerAge(id) < TimeSpan.FromSeconds(20))
            {
                time.Advance(TimeSpan.FromSeconds(1)); // the heartbeat measures the round trip; never ahead of the node's answers
            }

            return server.Manager.RoundTrip(id) > TimeSpan.Zero;
        }, "a measured round trip");

        // A heartbeat still in flight may measure again: compare the router against one round trip that did not change
        // while the router read it.
        var router = server.Host.Get<DeviceRouter>();
        var route = DeviceRoute.ViaSite(id);
        TimeSpan roundTrip = default, extra = default, timeout = default;
        await Eventually.TrueAsync(() =>
        {
            roundTrip = server.Manager.RoundTrip(id);
            extra = router.ExtraTimeout(route);
            timeout = router.Timeout(route, TimeSpan.FromSeconds(3));
            return roundTrip == server.Manager.RoundTrip(id);
        }, "a round trip that stays while the router reads it");
        Assert.Equal(roundTrip * 4, extra);
        Assert.Equal(TimeSpan.FromSeconds(3) + (roundTrip * 4), timeout);
    }

    [Fact]
    public async Task OfflineAfterThirtySecondsWithoutAnswerOnlineAfterSixtySecondsStable()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await SiteServer.StartAsync(time: time);
        var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        var folder = node.CopyFolder();
        try
        {
            await node.DisposeAsync();
            await Eventually.TrueAsync(() => !SessionAlive(server, id), "the node's connection gone");

            time.Advance(TimeSpan.FromSeconds(25));
            Assert.True(server.Manager.IsOnline(id)); // a short dropout is no Site offline
            time.Advance(TimeSpan.FromSeconds(6));
            await Eventually.TrueAsync(() => !server.Manager.IsOnline(id), "the site offline after 30 s");
            Assert.Contains(SiteAudit.Offline, await server.AuditActionsAsync());

            // The node comes back: online only after 60 s stable.
            await using var back = await SiteTestNode.StartAsync(folder: folder);
            await Eventually.TrueAsync(() => SessionAlive(server, id), "the node connected again");
            await server.AdvanceWhileAnsweringAsync(id, TimeSpan.FromSeconds(50));
            Assert.False(server.Manager.IsOnline(id));
            await Eventually.TrueAsync(() =>
            {
                if (server.Manager.AnswerAge(id) < TimeSpan.FromSeconds(20))
                {
                    time.Advance(TimeSpan.FromSeconds(1));
                }

                return server.Manager.IsOnline(id);
            }, "the site online after 60 s");
            Assert.Contains(SiteAudit.Online, await server.AuditActionsAsync());
        }
        finally
        {
            SiteTestNode.TryDelete(folder);
        }
    }

    [Fact]
    public async Task AfterAServerStartASiteIsStartingUntilItsNodeIsBackOrTheWindowPassed()
    {
        var data = TestServerHost.NewDataDirectory();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Guid id;
        string folder;
        await using (var first = await SiteServer.StartAsync(time: time, dataDirectory: data))
        {
            first.Host.KeepDataDirectory = true;
            await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = first.Address);
            id = await first.BindAsync(node);
            folder = node.CopyFolder();
        }

        try
        {
            var later = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var server = await SiteServer.StartAsync(time: later, dataDirectory: data);
            Assert.True(server.Manager.IsStarting(id));
            Assert.False(server.Manager.IsOnline(id));
            Assert.Equal(SiteTexts.Connecting, (await server.ListAsync()).Sites.Single().Message);

            later.Advance(TimeSpan.FromSeconds(119));
            Assert.True(server.Manager.IsStarting(id));
            later.Advance(TimeSpan.FromSeconds(2));
            await Eventually.TrueAsync(() => !server.Manager.IsStarting(id), "the start window over");
            Assert.False(server.Manager.IsOnline(id));
            Assert.Contains(SiteAudit.Offline, await server.AuditActionsAsync());
        }
        finally
        {
            SiteTestNode.TryDelete(folder);
            SiteTestNode.TryDelete(data);
        }
    }

    [Fact]
    public async Task AfterAServerStartANodeThatComesBackIsOnlineAtOnce()
    {
        var data = TestServerHost.NewDataDirectory();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Guid id;
        string folder;
        int port;
        await using (var first = await SiteServer.StartAsync(time: time, dataDirectory: data))
        {
            first.Host.KeepDataDirectory = true;
            await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = first.Address);
            id = await first.BindAsync(node);
            folder = node.CopyFolder();
            port = first.Manager.ListenPort;
        }

        try
        {
            // The second server listens on another port: point the node there (its identity and binding stay).
            await using var server = await SiteServer.StartAsync(time: new FakeTimeProvider(DateTimeOffset.UtcNow), dataDirectory: data);
            Assert.NotEqual(0, port);
            new NodeStore(folder).Update(c => c.ServerAddress = server.Address);
            await using var back = await SiteTestNode.StartAsync(folder: folder);

            await Eventually.TrueAsync(() => server.Manager.IsUsable(id), "the site online without the 60 s wait");
            Assert.DoesNotContain(SiteAudit.Offline, await server.AuditActionsAsync());
        }
        finally
        {
            SiteTestNode.TryDelete(folder);
            SiteTestNode.TryDelete(data);
        }
    }

    [Fact]
    public async Task ReplaceMovesTheDevicesThatAnswerThroughTheNewSite()
    {
        await using var device = LoopbackDevice.StartHttp();
        // The real probe (the test host fakes it): it reaches the device through the new site.
        await using var server = await SiteServer.StartAsync(configure: s =>
        {
            s.RemoveAll<VapixProbe>();
            s.AddSingleton(sp => new VapixProbe(TimeSpan.FromSeconds(5), router: sp.GetRequiredService<DeviceRouter>()));
        });
        await using var oldNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var oldId = await server.BindAsync(oldNode, "Old");
        await using var newNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "new-pc");
        newNode.Map("192.168.0.20", 80, device.EndPoint);
        var newId = await server.BindAsync(newNode, "New");
        var repository = server.Host.Get<DeviceRepository>();
        var answers = await repository.AddAsync(new Device { Serial = LoopbackDevice.Serial, Address = "192.168.0.20", Scheme = DeviceScheme.Http, SiteId = oldId, Status = SdkStatus.Ok }, CancellationToken.None);
        var silent = await repository.AddAsync(new Device { Serial = "B8A44F000009", Address = "192.168.0.99", Scheme = DeviceScheme.Http, SiteId = oldId, Status = SdkStatus.Ok }, CancellationToken.None);

        var reply = await server.Client.ReplaceSiteAsync(new Proto.ReplaceSiteRequest { Id = oldId.ToString(), NewSiteId = newId.ToString() });

        Assert.Equal(1, reply.Moved);
        Assert.Equal([silent.Id.ToString()], reply.StayedDeviceIds);
        Assert.Equal(newId, (await repository.GetAsync(answers.Id, CancellationToken.None))!.SiteId);
        Assert.Equal(oldId, (await repository.GetAsync(silent.Id, CancellationToken.None))!.SiteId);
        Assert.Contains(SiteAudit.Replaced, await server.AuditActionsAsync());
    }

    [Fact]
    public async Task WatchSitesSendsTheListAndItsChanges()
    {
        await using var server = await SiteServer.StartAsync();
        using var call = server.Client.WatchSites(new Proto.Empty());
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Empty(call.ResponseStream.Current.Sites);
        Assert.True(call.ResponseStream.Current.Accept);

        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        while (await call.ResponseStream.MoveNext())
        {
            if (call.ResponseStream.Current.Sites.Any(s => s.State == Proto.SiteState.Waiting))
            {
                break;
            }
        }

        Assert.Equal("site-pc", call.ResponseStream.Current.Sites.Single().Name);
    }

    [Fact]
    public async Task TheDeviceCountsAreReadOnlyWhenADeviceChangesItsSite()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        var repository = server.Host.Get<DeviceRepository>();
        await server.Manager.PageAsync(CancellationToken.None);
        var reads = server.Manager.DeviceCountReads;

        // Heartbeats, states and transfers push the page again: the counts come from memory.
        for (var i = 0; i < 5; i++)
        {
            server.Manager.RaiseList();
            await server.Manager.PageAsync(CancellationToken.None);
        }

        Assert.Equal(reads, server.Manager.DeviceCountReads);

        // A device joins the site: read, then from memory again. The feed delivers on its own thread: an event that arrives
        // while a read runs reads once more (it may not be in that read), so the reads are compared once they settle.
        var device = await repository.AddAsync(new Device { Serial = "B8A44F000001", Address = "192.168.0.20", SiteId = id, Status = SdkStatus.Ok }, CancellationToken.None);
        await Eventually.TrueAsync(async () => Count(await server.Manager.PageAsync(CancellationToken.None), id) == 1, "the device counted");
        var joined = await SettledReadsAsync(server);
        Assert.True(joined > reads);

        // A status change of the device keeps its site: nothing to read.
        await repository.UpdateAsync(device.Id, d => d.Status = SdkStatus.Unreachable, CancellationToken.None);
        Assert.Equal(1, Count(await server.Manager.PageAsync(CancellationToken.None), id));
        Assert.Equal(joined, await SettledReadsAsync(server));

        // The device moves to the server's own network: read again.
        await repository.UpdateAsync(device.Id, d => d.SiteId = null, CancellationToken.None);
        await Eventually.TrueAsync(async () => Count(await server.Manager.PageAsync(CancellationToken.None), id) == 0, "the device no longer counted");
        Assert.True(await SettledReadsAsync(server) > joined);
    }

    /// <summary>The count of reads once two pages in a row read nothing more (the feed's events are in).</summary>
    private static async Task<int> SettledReadsAsync(SiteServer server)
    {
        var settled = -1;
        await Eventually.TrueAsync(async () =>
        {
            var before = server.Manager.DeviceCountReads;
            await server.Manager.PageAsync(CancellationToken.None);
            await server.Manager.PageAsync(CancellationToken.None);
            settled = server.Manager.DeviceCountReads;
            return settled == before;
        }, "the device counts settled");
        return settled;
    }

    private static int Count(Proto.SiteList list, Guid siteId) => list.Sites.Single(s => s.Id == siteId.ToString()).DeviceCount;

    [Fact]
    public async Task OperatorsSeeTheSitesButChangeNothing()
    {
        await using var server = await SiteServer.StartAsync();
        var operatorInvoker = await server.Host.InvokerForUserAsync("op", Sdk.Plugins.UserRole.Operator);
        var client = new Proto.SiteService.SiteServiceClient(operatorInvoker);

        Assert.NotNull(await client.ListSitesAsync(new Proto.Empty()));
        await SiteAsserts.RefusedAsync(StatusCode.PermissionDenied, async () => await client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = false }));
        await SiteAsserts.RefusedAsync(StatusCode.PermissionDenied, async () =>
            await client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = "x", Name = "y", PairingCode = "ABCDEFGH" }));
        Assert.True(server.Manager.Accept);
    }

    [Fact]
    public async Task TurningOnSiteNodesWhileAnotherProgramHoldsThePortIsRefusedAndNothingIsStored()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var port = ((IPEndPoint)other.LocalEndpoint).Port;
        await using var server = await SiteServer.StartAsync(accept: false,
            options: new SiteOptions { ListenUrl = "127.0.0.1:" + port, WorkerWait = TimeSpan.FromSeconds(30), CallTimeout = TimeSpan.FromSeconds(30) });

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = true }));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(SiteTexts.ListenPortInUse(port), refused.Status.Detail);
        Assert.False(server.Manager.Accept);
        Assert.False(await server.Host.Get<ServerSettingsStore>().GetAsync<bool>(SiteSettings.Accept, CancellationToken.None));
    }

    [Fact]
    public async Task ABusyPortAtStartNeverStopsTheServerAndIsTriedAgainEvery30Seconds()
    {
        var folder = Support.TestServerHost.NewDataDirectory();
        try
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var options = new SiteOptions { ListenUrl = "127.0.0.1:" + port, WorkerWait = TimeSpan.FromSeconds(30), CallTimeout = TimeSpan.FromSeconds(30) };
            await using (var first = await SiteServer.StartAsync(dataDirectory: folder, options: options))
            {
                first.Host.KeepDataDirectory = true; // Accept site nodes is stored on
            }

            var other = new TcpListener(IPAddress.Loopback, port);
            other.Start();
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var restarted = await SiteServer.StartAsync(accept: false, dataDirectory: folder, time: time,
                options: new SiteOptions { ListenUrl = "127.0.0.1:" + port, Time = time, WorkerWait = TimeSpan.FromSeconds(30), CallTimeout = TimeSpan.FromSeconds(30) });

            // The server runs; site nodes cannot connect until the port is free.
            Assert.Equal(0, restarted.Manager.ListenPort);
            Assert.NotNull(await restarted.ListAsync());

            other.Stop();
            time.Advance(SiteManager.ListenRetry + TimeSpan.FromSeconds(1));
            await Eventually.TrueAsync(() => restarted.Manager.ListenPort == port, "the site port opens on the next try");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static bool SessionAlive(SiteServer server, Guid id) => server.Manager.HasSession(id);
}
