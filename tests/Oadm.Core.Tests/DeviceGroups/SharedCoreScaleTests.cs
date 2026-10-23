using System.Diagnostics;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The lists of a user of a device group at the device limit: the tag counts of the view and the event log filtered in the
/// query (30,000 devices, a group of 15,000, 50,000 log lines).
/// </summary>
public sealed class SharedCoreScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task TagsAndTheEventLogOfAGroupAtThirtyThousandDevices()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            ids.Add((await Devices.AddAsync(new Device
            {
                Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Status = DeviceStatus.Ok,
                AddedByAllDevices = true,
                Tags = [i % 2 == 0 ? "Even" : "Odd"],
            }, Ct)).Id);
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

        await using var log = new EventLog(Db.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>());
        const int Lines = 50_000;
        for (var i = 0; i < Lines; i++)
        {
            log.Write(EventLogKind.Event, EventLogResult.Info, "Event", ids[i % ids.Count], "device");
            if (i % 10_000 == 9_999)
            {
                await log.FlushAsync(Ct);
            }
        }

        await log.FlushAsync(Ct);
        var scope = new EventLogScope(await view.IdsAsync(Ct), new HashSet<Guid>());
        watch.Restart();
        var (page, total) = await log.ListAsync(0, 0, null, scope, Ct);
        var logList = watch.Elapsed;
        Assert.Equal(Lines / 2, total);
        Assert.Equal(EventLog.ListLimit, page.Count);
        Assert.All(page, l => Assert.True(scope.Covers(l)));

        output.WriteLine($"tag list of the group {tagList.TotalMilliseconds:N0} ms; event log page of the group ({total:N0} of {Lines:N0} lines) {logList.TotalMilliseconds:N0} ms");
        Assert.True(tagList < TimeSpan.FromSeconds(2), $"the tag list took {tagList}");
        Assert.True(logList < TimeSpan.FromSeconds(2), $"the event log took {logList}");
    }
}
