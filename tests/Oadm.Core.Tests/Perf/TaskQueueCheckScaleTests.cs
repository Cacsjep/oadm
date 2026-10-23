using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// The queue check of a group user's tasks at size (docs/spec/device-scope.md, Code (tasks), "Queue check"): 5,000 queued
/// tasks leave the queue, each reads its owner's current view by the user's id (one row), never the whole users table.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class TaskQueueCheckScaleTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    private const int TaskCount = 5_000;
    private const int OtherUsers = 500;

    [Fact]
    public async Task FiveThousandQueuedTasksOfAGroupUserLeaveTheQueueQuickly()
    {
        var deviceIds = new List<Guid>(TaskCount);
        await using (var ctx = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var rows = Enumerable.Range(0, TaskCount).Select(i => new Device
            {
                Serial = $"B8A44F{i:X6}",
                Address = $"10.1.{i / 250}.{i % 250}",
                Model = "P3265-V",
                Status = DeviceStatus.Ok,
                AddedByAllDevices = true,
            }).ToList();
            ctx.Devices.AddRange(rows);
            await ctx.SaveChangesAsync();
            deviceIds.AddRange(rows.Select(d => d.Id));
        }

        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync(deviceIds, Ct), ["Building B"], [], Ct);
        var group = (await SaveGroupAsync("B", tags: [await TagIdAsync("Building B")])).Group;
        for (var i = 0; i < OtherUsers; i++)
        {
            await Users.AddAsync($"user{i}", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        }

        var user = await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var caller = CallerOf(user);
        var view = await Views.ForAsync(caller, Ct);
        var registry = new PluginRegistry();
        var ran = 0;
        Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.queue", (_, _, _) =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        })
        { Limit = 8 }, new PluginOrigin("test", "1.0.0", null)));
        await using var engine = new TaskEngine(Db.Get<EfTaskStore>(), registry, Devices, new FakeVapixClientFactory()) { Views = Views };
        var devices = await view.GetAsync([.. (await view.IdsAsync(Ct))], Ct);

        IReadOnlyList<Guid> ids = [];
        await PerfScale.MeasureAsync(output, $"{TaskCount:N0} queued tasks of a group user leave the queue ({OtherUsers + 1:N0} users)", TimeSpan.FromSeconds(120), async () =>
        {
            ids = await engine.RunAsync("t.queue", devices, null, TaskCaller.Of(caller), Ct);
            foreach (var id in ids)
            {
                await engine.WaitForCompletionAsync(id, Ct);
            }
        });

        Assert.Equal(TaskCount, ids.Count);
        Assert.Equal(TaskCount, ran);
        Assert.Equal(TaskState.Done, (await engine.GetAsync(ids[^1], Ct))!.State);
    }
}
