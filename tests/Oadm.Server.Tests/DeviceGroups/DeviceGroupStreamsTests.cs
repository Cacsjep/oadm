using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Sites;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// The device list, live video and the Sites page of a user with a device group (docs/spec/device-scope.md): the feed adds
/// and removes devices that enter or leave the view, every id method refuses unknown ids for every user, a live video ends
/// when its device leaves the view, and the Sites page shows only the group's sites with its counts.
/// </summary>
public sealed class DeviceGroupStreamsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheDeviceFeedOfAGroupUserAddsAndRemovesDevicesWhileUsersWithAllDevicesSeeUpdates()
    {
        await using var host = await TestServerHost.StartAsync();
        var inside = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var outside = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        var group = await GroupUser.CreateAsync(host, inside.Id);

        using var cts = new CancellationTokenSource(Wait);
        using var mine = Watch(group.Devices);
        using var admins = Watch(host.Devices);
        Assert.Equal([inside.Id.ToString()], await SnapshotAsync(mine, cts.Token));
        Assert.Equal(2, (await SnapshotAsync(admins, cts.Token)).Count);

        // The tag of the group brings a device in (Added), its removal takes it out (Removed); outside changes stay outside.
        await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { outside.Id.ToString() }, Add = { GroupUser.Tag } });
        await NextAsync(mine, Proto.DeviceChanged.Types.Kind.Added, outside.Id, cts.Token);
        await NextAsync(admins, Proto.DeviceChanged.Types.Kind.Updated, outside.Id, cts.Token);

        await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { inside.Id.ToString() }, Remove = { GroupUser.Tag } });
        await NextAsync(mine, Proto.DeviceChanged.Types.Kind.Removed, inside.Id, cts.Token);
        await NextAsync(admins, Proto.DeviceChanged.Types.Kind.Updated, inside.Id, cts.Token);

        await host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { inside.Id.ToString() } });
        await NextAsync(admins, Proto.DeviceChanged.Types.Kind.Removed, inside.Id, cts.Token);
        await host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { outside.Id.ToString() } });
        await NextAsync(admins, Proto.DeviceChanged.Types.Kind.Removed, outside.Id, cts.Token);
        // The group's feed got nothing more of the device that had already left it.
        var skipped = await NextAsync(mine, Proto.DeviceChanged.Types.Kind.Removed, outside.Id, cts.Token);
        Assert.DoesNotContain(skipped, c => c.Device.Id == inside.Id.ToString());

        var list = await group.Devices.ListAsync(new Proto.Empty());
        Assert.Empty(list.Devices);
    }

    [Fact]
    public async Task NewRulesOfTheGroupEndTheFeedAndTheNextOneHasTheNewView()
    {
        await using var host = await TestServerHost.StartAsync();
        var inside = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var other = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        var group = await GroupUser.CreateAsync(host, inside.Id);
        using var cts = new CancellationTokenSource(Wait);
        using (var first = Watch(group.Devices))
        {
            Assert.Equal([inside.Id.ToString()], await SnapshotAsync(first, cts.Token));
            var groups = new Proto.DeviceGroupService.DeviceGroupServiceClient(host.Invoker);
            var saved = (await groups.ListAsync(new Proto.Empty())).Groups.Single();
            await groups.SaveAsync(new Proto.SaveDeviceGroupRequest
            {
                Id = saved.Id,
                Name = saved.Name,
                Revision = saved.Revision,
                Rules = new Proto.DeviceGroupRules { ThisServer = true },
            });
            await Assert.ThrowsAnyAsync<RpcException>(async () =>
            {
                while (await first.ResponseStream.MoveNext(cts.Token))
                {
                }

                throw new RpcException(new Status(StatusCode.Cancelled, "ended"));
            });
        }

        using var again = Watch(group.Devices);
        string[] both = [inside.Id.ToString(), other.Id.ToString()];
        Assert.Equal(both.Order(StringComparer.Ordinal), (await SnapshotAsync(again, cts.Token)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnknownIdsRefuseTheWholeRequestForEveryUserAndNothingRuns()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var unknown = Guid.NewGuid().ToString();

        var remove = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Devices.RemoveAsync(new Proto.DeviceIds { Ids = { device.Id.ToString(), unknown } }));
        Assert.Equal(StatusCode.NotFound, remove.StatusCode);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), remove.Status.Detail);
        Assert.NotNull(await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None));

        var refresh = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Devices.RefreshAsync(new Proto.DeviceIds { Ids = { device.Id.ToString(), unknown, Guid.NewGuid().ToString() } }));
        Assert.Equal(StatusCode.NotFound, refresh.StatusCode);
        Assert.Equal(DeviceMessages.NoLongerAvailable(2), refresh.Status.Detail);
        Assert.False(host.Get<DevicePollingService>().IsRefreshQueued(device.Id));

        var port = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { device.Id.ToString(), unknown }, Port = 8554 }));
        Assert.Equal(StatusCode.NotFound, port.StatusCode);
        Assert.Equal(554, (await host.LiveView.GetRtspPortAsync(new Proto.RtspPortRequest { DeviceIds = { device.Id.ToString() } })).Port);

        // Without ids Refresh takes every device of the caller's view.
        await host.Devices.RefreshAsync(new Proto.DeviceIds());
    }

    [Fact]
    public async Task ALiveVideoEndsWhenItsDeviceLeavesTheViewAndKeepsRunningForAllDevices()
    {
        var factory = new FrameSources();
        await using var host = await TestServerHost.StartAsync(configureServices: s =>
        {
            s.RemoveAll<ILiveVideoSourceFactory>();
            s.AddSingleton<ILiveVideoSourceFactory>(factory);
        });
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var group = await GroupUser.CreateAsync(host, device.Id);
        using var cts = new CancellationTokenSource(Wait);

        using var mine = new Proto.LiveViewService.LiveViewServiceClient(group.Invoker).Watch(Request(device.Id), cancellationToken: cts.Token);
        using var admins = host.LiveView.Watch(Request(device.Id), cancellationToken: cts.Token);
        await Eventually.TrueAsync(() => factory.Source is not null && host.Get<Core.DeviceGroups.DeviceViewWatcher>().Count == 1, "the camera stream open");
        factory.Source!.Emit();
        Assert.True(await mine.ResponseStream.MoveNext(cts.Token));
        Assert.True(await admins.ResponseStream.MoveNext(cts.Token));

        await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Remove = { GroupUser.Tag } });
        var ended = await Assert.ThrowsAsync<RpcException>(() => mine.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(StatusCode.NotFound, ended.StatusCode);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), ended.Status.Detail);

        factory.Source.Emit();
        Assert.True(await admins.ResponseStream.MoveNext(cts.Token));

        // Out of the view now: the same answer as an unknown device, and the camera is not asked.
        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await new Proto.LiveViewService.LiveViewServiceClient(group.Invoker).ListSourcesAsync(new Proto.LiveViewSourcesRequest { DeviceId = device.Id.ToString() }));
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Status.Detail);
    }

    [Fact]
    public async Task TheSitesPageOfAGroupUserShowsOnlyTheGroupsSitesWithItsCounts()
    {
        await using var server = await SiteServer.StartAsync();
        await using var nodeB = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var b = await server.BindAsync(nodeB, "Building B");
        await using var nodeW = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-2");
        var w = await server.BindAsync(nodeW, "Warehouse");
        var repository = server.Host.Get<DeviceRepository>();
        for (var i = 1; i <= 5; i++)
        {
            await repository.AddAsync(new Device { Serial = FakeSerials.Make(i), Address = $"10.9.0.{i}", SiteId = i <= 3 ? b : w, AddedByAllDevices = true }, CancellationToken.None);
        }

        var groups = new Proto.DeviceGroupService.DeviceGroupServiceClient(server.Host.Invoker);
        var group = await groups.SaveAsync(new Proto.SaveDeviceGroupRequest
        {
            Name = "Building B",
            Rules = new Proto.DeviceGroupRules { SiteIds = { b.ToString() }, ExceptSerials = { FakeSerials.Make(1) } },
        });
        var invoker = (await GroupCallers.UserAsync(server.Host, "anna", Guid.Parse(group.Id))).Invoker;
        var client = new Proto.SiteService.SiteServiceClient(invoker);

        await Eventually.TrueAsync(async () => (await server.ListAsync()).Sites.Sum(s => s.DeviceCount) == 5, "the device counts of both sites");
        var mine = await client.ListSitesAsync(new Proto.Empty());
        var site = Assert.Single(mine.Sites);
        Assert.Equal(b.ToString(), site.Id);
        Assert.Equal(2, site.DeviceCount); // the group's devices of the site, the excepted one left out
        Assert.Empty(site.GroupNames);

        using var call = client.WatchSites(new Proto.Empty(), cancellationToken: new CancellationTokenSource(Wait).Token);
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal([b.ToString()], call.ResponseStream.Current.Sites.Select(s => s.Id));

        // Users with all devices: every site, the groups that use it.
        var all = await server.ListAsync();
        Assert.Equal(["Building B"], all.Sites.Single(s => s.Id == b.ToString()).GroupNames);
        Assert.Empty(all.Sites.Single(s => s.Id == w.ToString()).GroupNames);
        Assert.Equal(3, all.Sites.Single(s => s.Id == b.ToString()).DeviceCount);
    }

    private static AsyncServerStreamingCall<Proto.DeviceChanged> Watch(Proto.DeviceService.DeviceServiceClient client) =>
        client.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, deadline: DateTime.UtcNow.Add(Wait));

    private static async Task<List<string>> SnapshotAsync(AsyncServerStreamingCall<Proto.DeviceChanged> call, CancellationToken ct)
    {
        var ids = new List<string>();
        while (await call.ResponseStream.MoveNext(ct) && call.ResponseStream.Current.Kind != Proto.DeviceChanged.Types.Kind.SnapshotEnd)
        {
            Assert.Equal(Proto.DeviceChanged.Types.Kind.Added, call.ResponseStream.Current.Kind);
            ids.Add(call.ResponseStream.Current.Device.Id);
        }

        return ids;
    }

    /// <summary>
    /// The next change of <paramref name="deviceId"/> is <paramref name="kind"/>; returns the changes of other devices
    /// before it (status updates).
    /// </summary>
    private static async Task<List<Proto.DeviceChanged>> NextAsync(AsyncServerStreamingCall<Proto.DeviceChanged> call, Proto.DeviceChanged.Types.Kind kind, Guid deviceId, CancellationToken ct)
    {
        var skipped = new List<Proto.DeviceChanged>();
        while (await call.ResponseStream.MoveNext(ct))
        {
            var change = call.ResponseStream.Current;
            if (change.Device.Id == deviceId.ToString())
            {
                Assert.Equal(kind, change.Kind);
                return skipped;
            }

            skipped.Add(change);
        }

        Assert.Fail("The stream ended.");
        return skipped;
    }

    private static Proto.LiveViewRequest Request(Guid device) =>
        new() { DeviceId = device.ToString(), MaxWidth = 640, MaxHeight = 360, Fps = 10, AcceptedCodecs = { Proto.VideoCodec.H264 } };

    /// <summary>A user whose role allows everything, limited to the group of the tag <see cref="Tag"/>.</summary>
    internal sealed record GroupUser(CallInvoker Invoker, Proto.DeviceService.DeviceServiceClient Devices)
    {
        public const string Tag = "Group B";

        public static async Task<GroupUser> CreateAsync(TestServerHost host, params Guid[] tagged)
        {
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { tagged.Select(t => t.ToString()) }, Add = { Tag } });
            var user = await GroupCallers.ForTagAsync(host, "anna", Tag);
            return new GroupUser(user.Invoker, new Proto.DeviceService.DeviceServiceClient(user.Invoker));
        }
    }

    /// <summary>One camera stream that sends a frame when the test says so.</summary>
    private sealed class FrameSources : ILiveVideoSourceFactory
    {
        public FrameSource? Source { get; private set; }

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(new LiveViewCapabilities([VideoCodecKind.H264], [new(640, 360)], null));

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
        {
            Source = new FrameSource(key);
            return Task.FromResult<ILiveVideoSource>(Source);
        }
    }

    private sealed class FrameSource(LiveStreamKey key) : ILiveVideoSource
    {
        private readonly Channel<LiveVideoFrame> _frames = Channel.CreateUnbounded<LiveVideoFrame>();
        private long _timestamp;

        public LiveStreamKey Key { get; } = key;

        public void Emit() =>
            _frames.Writer.TryWrite(new(VideoCodecKind.H264, [0, 0, 0, 1, 0x65], true, 640, 360, DateTimeOffset.UtcNow, null, Interlocked.Increment(ref _timestamp) * 3000));

        public async IAsyncEnumerable<LiveVideoFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
