using Grpc.Core;

using Oadm.Core.Plugins;
using Oadm.Plugins.Restart;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

public sealed class TaskServiceTests
{
    private static void RegisterFastRestart(TestServerHost host)
    {
        var plugin = new RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System);
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("oadm.restart", "1.0.0", null)));
    }

    [Fact]
    public async Task ListTaskPluginsReportsRunnableDevicesAndHidesBuiltIns()
    {
        await using var host = await TestServerHost.StartAsync();
        RegisterFastRestart(host);
        var ok = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var noCredentials = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, password: null, status: SdkDeviceStatus.CredentialsRequired);

        var plugins = await host.Tasks.ListTaskPluginsAsync(new Proto.Empty());

        Assert.DoesNotContain(plugins.Plugins, p => p.Id == AddDevicesTaskPlugin.PluginId);
        var restart = Assert.Single(plugins.Plugins, p => p.Id == RestartTaskPlugin.PluginId);
        Assert.Equal("Restart", restart.DisplayName);
        Assert.True(restart.ShowInToolbar);
        Assert.False(restart.RequiresDialog);
        Assert.Equal(string.Empty, restart.OwnerCorePluginId);
        Assert.Equal([ok.Id.ToString()], restart.RunnableDeviceIds);
        Assert.DoesNotContain(noCredentials.Id.ToString(), restart.RunnableDeviceIds);
    }

    [Fact]
    public async Task RunRestartTaskReportsProgressAndRefreshesTheDevice()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        RegisterFastRestart(host);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var watch = host.Tasks.Watch(new Proto.Empty(), cancellationToken: cts.Token);
        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest
        {
            PluginId = RestartTaskPlugin.PluginId,
            DeviceIds = { device.Id.ToString() },
            Owner = "WS01/alice",
        });

        var seen = new List<Proto.TaskChanged>();
        await foreach (var change in watch.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (change.Task.Id != run.TaskId)
            {
                continue;
            }

            seen.Add(change);
            if (change.Task.State is Proto.TaskState.Done or Proto.TaskState.Failed or Proto.TaskState.Cancelled)
            {
                break;
            }
        }

        var final = seen[^1].Task;
        Assert.Equal(Proto.TaskState.Done, final.State);
        Assert.Equal(100, final.Progress);
        Assert.Equal("WS01/alice", final.Owner);
        Assert.Equal("Restart", final.Name);
        Assert.Equal(Proto.TaskChanged.Types.Kind.Added, seen[0].Kind);
        Assert.Contains(seen, c => c.Task.State == Proto.TaskState.Running && c.Task.Progress is > 0 and < 100);
        Assert.Equal(1, camera.RestartCalls);

        // The finished task triggers a full refresh of its device.
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, device.Id.ToString())).HasDhcpEnabled,
            "refresh after task");

        // Persisted, and deletable.
        var listed = Assert.Single((await host.Tasks.ListAsync(new Proto.Empty())).Tasks);
        Assert.Equal(Proto.TaskState.Done, listed.State);
        await host.Tasks.DeleteAsync(new Proto.TaskIdRequest { TaskId = run.TaskId });
        Assert.Empty((await host.Tasks.ListAsync(new Proto.Empty())).Tasks);
    }

    [Fact]
    public async Task CancelStopsARunningTask()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        camera.RestartDownRequests = int.MaxValue / 2; // never comes back
        await using var host = await TestServerHost.StartAsync(network);
        RegisterFastRestart(host);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(camera.RestartCalls == 1),"restart sent");
        await host.Tasks.CancelAsync(new Proto.TaskIdRequest { TaskId = run.TaskId });

        var final = await TestHelpers.WaitForTaskAsync(host, run.TaskId);
        Assert.Equal(Proto.TaskState.Cancelled, final.State);
        Assert.False(string.IsNullOrEmpty(final.Owner)); // falls back to the peer
    }

    [Fact]
    public async Task InvalidRequestsMapToStatusCodes()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var unknownPlugin = await Assert.ThrowsAsync<RpcException>(() =>
            host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = "nope", DeviceIds = { device.Id.ToString() } }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, unknownPlugin.StatusCode);

        var noDevices = await Assert.ThrowsAsync<RpcException>(() =>
            host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = AddDevicesTaskPlugin.PluginId }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, noDevices.StatusCode);

        var cancelUnknown = await Assert.ThrowsAsync<RpcException>(() =>
            host.Tasks.CancelAsync(new Proto.TaskIdRequest { TaskId = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, cancelUnknown.StatusCode);

        var deleteUnknown = await Assert.ThrowsAsync<RpcException>(() =>
            host.Tasks.DeleteAsync(new Proto.TaskIdRequest { TaskId = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, deleteUnknown.StatusCode);
    }
}
