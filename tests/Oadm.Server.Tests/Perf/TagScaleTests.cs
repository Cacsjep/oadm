using Oadm.Core.Devices;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>Tagging 5,000 devices: one call, one transaction, one change per device.</summary>
[Trait("Category", "Perf")]
public sealed class TagScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SetDeviceTagsRenameAndDeleteOn5000DevicesAreOneCallEach()
    {
        await using var host = await TestServerHost.StartAsync();
        var ids = await ServerScale.SeedDevicesAsync(host);
        var request = new Proto.SetDeviceTagsRequest { Add = { "Building A", "PTZ", "Outdoor" } };
        request.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        using var subscription = host.Get<DeviceRepository>().Changes.Subscribe();

        Proto.SetDeviceTagsReply reply = null!;
        await ServerScale.MeasureAsync(output, "SetDeviceTags, 5,000 devices x 3 tags, one call", TimeSpan.FromSeconds(15), async () =>
            reply = await host.Tags.SetDeviceTagsAsync(request));
        Assert.Equal(ServerScale.Devices, reply.DevicesChanged);
        Assert.Equal(3, reply.Created.Count);

        var published = 0;
        while (subscription.Reader.TryRead(out var change))
        {
            published += change.Kind == DeviceChangeKind.Updated ? 1 : 0;
        }

        Assert.Equal(ServerScale.Devices, published);

        Proto.TagList list = null!;
        await ServerScale.MeasureAsync(output, "List tags with device counts, 5,000 devices", TimeSpan.FromSeconds(5), async () =>
            list = await host.Tags.ListAsync(new Proto.Empty()));
        Assert.All(list.Tags, t => Assert.Equal(ServerScale.Devices, t.DeviceCount));

        await ServerScale.MeasureAsync(output, "Rename a tag on 5,000 devices", TimeSpan.FromSeconds(15), async () =>
            await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Building A", NewName = "Building B" }));
        var device = await host.Get<DeviceRepository>().GetAsync(ids[^1], CancellationToken.None);
        Assert.Equal(["Building B", "Outdoor", "PTZ"], device!.Tags);

        await ServerScale.MeasureAsync(output, "Delete a tag on 5,000 devices", TimeSpan.FromSeconds(15), async () =>
            await host.Tags.DeleteAsync(new Proto.TagName { Name = "PTZ" }));
        device = await host.Get<DeviceRepository>().GetAsync(ids[0], CancellationToken.None);
        Assert.Equal(["Building B", "Outdoor"], device!.Tags);
    }
}
