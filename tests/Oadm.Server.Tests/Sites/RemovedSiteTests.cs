using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Sites;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Sites;
using Oadm.Core.Vapix;
using Oadm.Server.Sites;
using Oadm.SiteNode.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// "Remove site" with Keep devices (docs/spec/sites.md, Server side: SiteAdmin): the site leaves a tombstone, so its devices are named
/// "&lt;site&gt; (removed)" and the Sites page lists it as Removed with Replace with and Remove devices until its last
/// device left; its waiting entries go.
/// </summary>
public sealed class RemovedSiteTests
{
    [Fact]
    public async Task ARemovedSiteWithKeptDevicesIsListedUntilItsDevicesAreRemoved()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node, "Building B");
        var repository = server.Host.Get<DeviceRepository>();
        var device = await repository.AddAsync(new Device { Serial = "B8A44F000011", Address = "192.168.0.20", SiteId = id, Status = SdkStatus.Ok }, CancellationToken.None);
        var waiting = server.Host.Get<WaitingDeviceStore>();
        await waiting.AddAsync([new WaitingLine("192.168.0.40", null, null, null, [], id)], "Import from file", "admin", CancellationToken.None);

        await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = true });

        var row = Assert.Single((await server.ListAsync()).Sites, s => s.Id == id.ToString());
        Assert.Equal((Proto.SiteState.Removed, "Building B", 1, SiteTexts.RemovedMessage), (row.State, row.Name, row.DeviceCount, row.Message));
        var kept = (await repository.GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal((SdkStatus.SiteOffline, "Building B (removed)"), (kept.Status, kept.SiteName));
        Assert.Equal(0, await waiting.CountAsync(CancellationToken.None)); // nothing reaches that site any more
        Assert.True(server.Manager.IsRemoved(id));

        // Keep devices again makes no sense: only Remove devices (or Replace with).
        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = true }));
        Assert.Equal((StatusCode.FailedPrecondition, SiteTexts.RemovedKeepsDevices), (refused.StatusCode, refused.Status.Detail));

        var removed = await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = false });

        Assert.Equal(1, removed.DevicesRemoved);
        Assert.Null(await repository.GetAsync(device.Id, CancellationToken.None));
        Assert.DoesNotContain((await server.ListAsync()).Sites, s => s.Id == id.ToString());
        Assert.False(server.Manager.IsRemoved(id));
    }

    [Fact]
    public async Task ATombstoneWithoutDevicesIsDroppedAndOneWithDevicesSurvivesARestart()
    {
        var folder = Support.TestServerHost.NewDataDirectory();
        Guid id;
        Guid deviceId;
        try
        {
            await using (var server = await SiteServer.StartAsync(dataDirectory: folder))
            {
                server.Host.KeepDataDirectory = true;
                await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
                id = await server.BindAsync(node, "Warehouse");
                var repository = server.Host.Get<DeviceRepository>();
                deviceId = (await repository.AddAsync(new Device { Serial = "B8A44F000012", Address = "192.168.0.21", SiteId = id, Status = SdkStatus.Ok }, CancellationToken.None)).Id;
                await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = true });
            }

            await using (var restarted = await SiteServer.StartAsync(dataDirectory: folder))
            {
                Assert.True(restarted.Manager.IsRemoved(id));
                Assert.Equal("Warehouse (removed)", (await restarted.Host.Get<DeviceRepository>().GetAsync(deviceId, CancellationToken.None))!.SiteName);

                // The device leaves another way (the device list's Remove): the next list forgets the site.
                await restarted.Host.Get<DeviceRepository>().RemoveManyAsync([deviceId], CancellationToken.None);
                Assert.DoesNotContain((await restarted.ListAsync()).Sites, s => s.Id == id.ToString());
                Assert.False(restarted.Manager.IsRemoved(id));
            }
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
                    // The second host removed it or a log file is still closing.
                }
            }
        }
    }

    [Fact]
    public async Task ReplaceMovesTheDevicesOfARemovedSiteAndForgetsIt()
    {
        await using var device = LoopbackDevice.StartHttp();
        await using var server = await SiteServer.StartAsync(configure: s =>
        {
            s.RemoveAll<VapixProbe>();
            s.AddSingleton(sp => new VapixProbe(TimeSpan.FromSeconds(5), router: sp.GetRequiredService<DeviceRouter>()));
        });
        await using var oldNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var oldId = await server.BindAsync(oldNode, "Old");
        var repository = server.Host.Get<DeviceRepository>();
        var answers = await repository.AddAsync(new Device { Serial = LoopbackDevice.Serial, Address = "192.168.0.20", Scheme = DeviceScheme.Http, SiteId = oldId, Status = SdkStatus.Ok }, CancellationToken.None);
        await server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = oldId.ToString(), KeepDevices = true });
        await using var newNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "new-pc");
        newNode.Map("192.168.0.20", 80, device.EndPoint);
        var newId = await server.BindAsync(newNode, "New");

        var reply = await server.Client.ReplaceSiteAsync(new Proto.ReplaceSiteRequest { Id = oldId.ToString(), NewSiteId = newId.ToString() });

        Assert.Equal(1, reply.Moved);
        Assert.Empty(reply.StayedDeviceIds);
        var moved = (await repository.GetAsync(answers.Id, CancellationToken.None))!;
        Assert.Equal((newId, "New"), (moved.SiteId, moved.SiteName));
        Assert.False(server.Manager.IsRemoved(oldId));
        Assert.DoesNotContain((await server.ListAsync()).Sites, s => s.Id == oldId.ToString());
    }

    [Fact]
    public async Task ReplaceMovesTheWaitingEntriesToTheNewSite()
    {
        await using var server = await SiteServer.StartAsync();
        await using var oldNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var oldId = await server.BindAsync(oldNode, "Old");
        await using var newNode = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "new-pc");
        var newId = await server.BindAsync(newNode, "New");
        var waiting = server.Host.Get<WaitingDeviceStore>();
        await waiting.AddAsync([
            new WaitingLine("192.168.0.40", null, null, null, [], oldId),
            new WaitingLine("192.168.0.41", null, null, null, [], oldId),
            new WaitingLine("192.168.0.41", null, null, null, [], newId), // the new site keeps this address already
        ], "Import from file", "admin", CancellationToken.None);

        await server.Client.ReplaceSiteAsync(new Proto.ReplaceSiteRequest { Id = oldId.ToString(), NewSiteId = newId.ToString() });

        var page = await waiting.ListAsync(null, 0, CancellationToken.None);
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, i => Assert.Equal(newId, i.SiteId));
        Assert.Equal(["192.168.0.40", "192.168.0.41"], page.Items.Select(i => i.Address).Order(StringComparer.Ordinal));
    }
}
