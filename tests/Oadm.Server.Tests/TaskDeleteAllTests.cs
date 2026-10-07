using Grpc.Core;

using Oadm.Core.Plugins;
using Oadm.Plugins.Restart;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

public sealed class TaskDeleteAllTests
{
    private static void RegisterFastRestart(TestServerHost host)
    {
        var plugin = new RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System);
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("oadm.restart", "1.0.0", null)));
    }

    [Fact]
    public async Task DeleteAllCancelsRunningTasksAndRemovesTheWholeHistory()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        var stuck = network.Add("10.9.0.2", FakeSerials.Make(2), "pw");
        stuck.RestartDownRequests = int.MaxValue / 2; // never comes back, the task stays running
        await using var host = await TestServerHost.StartAsync(network);
        RegisterFastRestart(host);
        var fast = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var slow = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);

        var done = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { fast.Id.ToString() } });
        Assert.Equal(Proto.TaskState.Done, (await TestHelpers.WaitForTaskAsync(host, done.TaskIds[0])).State);
        var running = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { slow.Id.ToString() } });
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(stuck.RestartCalls == 1), "restart sent");

        // Includes the AddDevices tasks created by AddDeviceAsync.
        var before = (await host.Tasks.ListAsync(new Proto.Empty())).Tasks.Select(t => t.Id).ToHashSet();
        Assert.Contains(done.TaskIds[0], before);
        Assert.Contains(running.TaskIds[0], before);

        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var watch = host.Tasks.Watch(new Proto.Empty(), cancellationToken: cts.Token);
        var snapshot = new HashSet<string>();
        var removed = new HashSet<string>();
        var deleteAll = Task.Run(async () =>
        {
            // Start only once the snapshot is through, so every Removed arrives on the live stream.
            await TestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (snapshot)
                    {
                        return Task.FromResult(snapshot.Count >= before.Count);
                    }
                },
                "watch snapshot");
            return await host.Tasks.DeleteAllAsync(new Proto.Empty());
        });

        await foreach (var change in watch.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (change.Kind == Proto.TaskChanged.Types.Kind.Added)
            {
                lock (snapshot)
                {
                    snapshot.Add(change.Task.Id);
                }
            }
            else if (change.Kind == Proto.TaskChanged.Types.Kind.Removed)
            {
                removed.Add(change.Task.Id);
                if (removed.IsSupersetOf(before))
                {
                    break;
                }
            }
        }

        var reply = await deleteAll;
        Assert.Equal(before.Count, reply.Deleted);
        Assert.Empty((await host.Tasks.ListAsync(new Proto.Empty())).Tasks);
        Assert.Equal(1, stuck.RestartCalls);
    }

    [Fact]
    public async Task DeleteAllOnAnEmptyHistoryDeletesNothing()
    {
        await using var host = await TestServerHost.StartAsync();

        var reply = await host.Tasks.DeleteAllAsync(new Proto.Empty());

        Assert.Equal(0, reply.Deleted);
        Assert.Empty((await host.Tasks.ListAsync(new Proto.Empty())).Tasks);
    }
}
