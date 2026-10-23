using System.Diagnostics;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The lists of a user of a device group at the device limit: the tag counts of the view (30,000 devices, a group of
/// 15,000).
/// </summary>
public sealed class SharedCoreScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task TheTagsOfAGroupAtThirtyThousandDevices()
    {
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            await Devices.AddAsync(new Device
            {
                Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Status = DeviceStatus.Ok,
                AddedByAllDevices = true,
                Tags = [i % 2 == 0 ? "Even" : "Odd"],
            }, Ct);
        }

        var even = (await Tags.CreateAsync("Even", TagColor.None, Ct)).Id;
        await Tags.CreateAsync("Odd", TagColor.None, Ct);
        var group = await SaveGroupAsync("Even", tags: [even]);
        var view = await Views.ForScopeAsync(DeviceScope.Group(group.Group.Id), "operator", Ct);

        var watch = Stopwatch.StartNew();
        var tags = await Tags.ListAsync(view, Ct);
        var tagList = watch.Elapsed;
        Assert.Equal(DeviceLimits.MaxDevices / 2, tags.Single(t => t.Name == "Even").DeviceCount);
        Assert.Equal(0, tags.Single(t => t.Name == "Odd").DeviceCount);

        output.WriteLine($"tag list of the group {tagList.TotalMilliseconds:N0} ms");
        Assert.True(tagList < TimeSpan.FromSeconds(2), $"the tag list took {tagList}");
    }
}
