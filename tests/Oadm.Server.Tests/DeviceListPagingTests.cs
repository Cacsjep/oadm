using Oadm.Server.Devices;
using Oadm.Server.Tests.Perf;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The device list is read from the database in pages (docs/30kspec.md, server memory): every device once, in serial order,
/// over several pages.
/// </summary>
public sealed class DeviceListPagingTests
{
    [Fact]
    public async Task TheWatchSnapshotSendsEveryDeviceOnceOverSeveralPages()
    {
        await using var host = await TestServerHost.StartAsync();
        var count = DeviceGrpcService.SnapshotPageSize * 2 + 300;
        var ids = await ServerScale.SeedDevicesAsync(host, count);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var received = new List<Proto.Device>();
        using (var call = host.Devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, cancellationToken: cts.Token))
        {
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                if (call.ResponseStream.Current.Kind == Proto.DeviceChanged.Types.Kind.SnapshotEnd)
                {
                    break;
                }

                received.Add(call.ResponseStream.Current.Device);
            }
        }

        Assert.Equal(count, received.Count);
        Assert.Equal(ids.Select(i => i.ToString()).Order(), received.Select(d => d.Id).Order());
        Assert.Equal(received.Select(d => d.Serial).Order(StringComparer.Ordinal), received.Select(d => d.Serial));
        Assert.All(received, d => Assert.Equal(ServerScale.ApisPerDevice - (d.Apis.Count < ServerScale.ApisPerDevice ? 1 : 0), d.Apis.Count));
    }
}
