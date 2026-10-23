using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Server.Tests.DeviceGroups;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// The device feed of a user with a device group at 30,000 devices (docs/spec/device-scope.md, Tests): the snapshot of a
/// group of 3,000 reads only the group's rows, and a burst of changes of every device reaches the group's stream as its
/// own changes only, with devices that enter or leave the group as Added and Removed.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class DeviceGroupWatchScaleTests(ITestOutputHelper output)
{
    private const int GroupSize = 3_000;
    private const int Moved = 1_000;

    [Fact]
    public async Task TheFeedOfAGroupOf3000In30000DevicesIsFastAndCarriesOnlyTheGroup()
    {
        await using var host = await TestServerHost.StartAsync();
        var ids = await ServerScale.SeedDevicesAsync(host);
        var group = await DeviceGroupStreamsTests.GroupUser.CreateAsync(host, [.. ids.Take(GroupSize)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var mine = group.Devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, cancellationToken: cts.Token);
        var count = 0;
        await ServerScale.MeasureAsync(output, $"Group Watch snapshot, {GroupSize:N0} of {ServerScale.Devices:N0} devices, until SNAPSHOT_END", TimeSpan.FromSeconds(8), async () =>
        {
            while (await mine.ResponseStream.MoveNext(cts.Token) && mine.ResponseStream.Current.Kind != Proto.DeviceChanged.Types.Kind.SnapshotEnd)
            {
                count++;
            }
        });
        Assert.Equal(GroupSize, count);

        // A burst over every device (a full refresh round): the group's stream gets its own 3,000 updates only.
        var inGroup = ids.Take(GroupSize).Select(i => i.ToString()).ToHashSet();
        var feed = host.Get<IDeviceChangeFeed>();
        var rows = await host.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None);
        var updates = 0;
        await ServerScale.MeasureAsync(output, $"Burst of {ServerScale.Devices:N0} updates, filtered to the group's {GroupSize:N0}", TimeSpan.FromSeconds(5), async () =>
        {
            feed.PublishMany([.. rows.Select(d => new DeviceChange(DeviceChangeKind.Updated, d.Id, d))]);
            var seen = new HashSet<string>();
            while (seen.Count < GroupSize && await mine.ResponseStream.MoveNext(cts.Token))
            {
                var change = mine.ResponseStream.Current;
                Assert.Contains(change.Device.Id, inGroup);
                seen.Add(change.Device.Id);
                updates++;
            }
        });
        Assert.True(updates >= GroupSize);

        // 1,000 devices enter the group (one tag write) and 1,000 leave it: Added and Removed.
        var entering = ids.Skip(GroupSize).Take(Moved).ToList();
        var leaving = ids.Take(Moved).ToList();
        var tags = host.Get<DeviceTagStore>();
        await ServerScale.MeasureAsync(output, $"{Moved:N0} devices enter and {Moved:N0} leave the group", TimeSpan.FromSeconds(15), async () =>
        {
            await tags.SetDeviceTagsAsync(entering, [DeviceGroupStreamsTests.GroupUser.Tag], [], cts.Token);
            await tags.SetDeviceTagsAsync(leaving, [], [DeviceGroupStreamsTests.GroupUser.Tag], cts.Token);
            var added = new HashSet<string>();
            var removed = new HashSet<string>();
            while ((added.Count < Moved || removed.Count < Moved) && await mine.ResponseStream.MoveNext(cts.Token))
            {
                var change = mine.ResponseStream.Current;
                if (change.Kind == Proto.DeviceChanged.Types.Kind.Added)
                {
                    added.Add(change.Device.Id);
                }
                else if (change.Kind == Proto.DeviceChanged.Types.Kind.Removed)
                {
                    removed.Add(change.Device.Id);
                }
            }

            Assert.Equal(entering.Select(i => i.ToString()).Order(StringComparer.Ordinal), added.Order(StringComparer.Ordinal));
            Assert.Equal(leaving.Select(i => i.ToString()).Order(StringComparer.Ordinal), removed.Order(StringComparer.Ordinal));
        });
    }
}
