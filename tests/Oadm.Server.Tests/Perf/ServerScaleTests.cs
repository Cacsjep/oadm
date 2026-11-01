using System.Diagnostics;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Perf;

/// <summary>Scale numbers of the server tests (see Oadm.Core.Tests/Perf for the store and engine ones).</summary>
internal static class ServerScale
{
    public const int Devices = DeviceLimits.MaxDevices;
    public const int Tasks = 50_000;
    public const int ApisPerDevice = 100;
    public const int Plugins = 10;

    /// <summary>The scale tests run one at a time: at 100,000 devices parallel servers slow each other down several times.</summary>
    public const string OneAtATime = "Perf one at a time";

    public static async Task<TimeSpan> MeasureAsync(ITestOutputHelper output, string what, TimeSpan budget, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        watch.Stop();
        output.WriteLine($"{what}: {watch.Elapsed.TotalMilliseconds:F0} ms (budget {budget.TotalMilliseconds:F0} ms)");
        Assert.True(watch.Elapsed < budget, $"{what} took {watch.Elapsed.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms");
        return watch.Elapsed;
    }

    /// <summary>Inserts devices in one transaction: every 20th one lacks "user-management" and every 50th is unreachable.</summary>
    public static async Task<List<Guid>> SeedDevicesAsync(TestServerHost host, int count = Devices)
    {
        var apis = Enumerable.Range(0, ApisPerDevice).Select(i => new DeviceApi($"api-{i:D3}", "1.0", $"Some API number {i}", "official")).ToList();
        var withoutUsers = apis.Where(a => a.Id != "api-000").ToList();
        await using var db = await host.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Devices.Add(new Device
            {
                Id = id,
                Serial = $"ACCC8E{i:X6}",
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Scheme = DeviceScheme.Http,
                Status = i % 50 == 0 ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.Ok,
                Model = "P3265-V",
                FirmwareVersion = "12.11.77",
                ProductType = "Dome Camera",
                Category = DeviceCategory.Camera,
                Apis = i % 20 == 0 ? withoutUsers : apis,
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Ten menu task plugins with typical CanRun checks (status and an API) and their reasons.</summary>
    public static void RegisterPlugins(TestServerHost host, int count = Plugins, string? lastApi = null)
    {
        var registry = host.Get<PluginRegistry>();
        for (var i = 0; i < count; i++)
        {
            var api = i == count - 1 && lastApi is not null ? lastApi : i % 2 == 0 ? "api-000" : "api-050";
            Assert.True(registry.RegisterTaskPlugin(new ApiTaskPlugin($"perf.task{i}", api), new PluginOrigin("perf", "1.0.0", null)));
        }
    }

    private sealed class ApiTaskPlugin(string id, string api) : ITaskPlugin
    {
        public string Id => id;

        public string DisplayName => "Perf " + id;

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => device.Status == SdkDeviceStatus.Ok && device.Apis.FindApi(api) is not null;

        public string? NotSupportedReason(IDeviceInfo device) =>
            TaskSupportReasons.ForStatus(device.Status, requireOk: true) ?? TaskSupportReasons.NeedsApi("the API " + api, device);

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>Runs the scale tests one after another (<see cref="ServerScale.OneAtATime"/>).</summary>
[CollectionDefinition(ServerScale.OneAtATime, DisableParallelization = true)]
public sealed class ServerPerfOneAtATime;

/// <summary>One server with 100,000 devices (100 APIs each), 10 task plugins and 50,000 finished tasks.</summary>
public sealed class ScaleServerFixture : IAsyncLifetime
{
    internal TestServerHost Host { get; private set; } = null!;

    public IReadOnlyList<Guid> DeviceIds { get; private set; } = [];

    public async Task InitializeAsync()
    {
        Host = await TestServerHost.StartAsync();
        DeviceIds = await ServerScale.SeedDevicesAsync(Host);
        ServerScale.RegisterPlugins(Host);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var tasks = Enumerable.Range(0, ServerScale.Tasks).Select(i => new TaskRecord(
            Guid.NewGuid(), "perf.task0", "Perf task", TaskState.Done, "tester", start.AddSeconds(i), start.AddSeconds(i), start.AddSeconds(i + 1), 100, null,
            [new TaskDeviceRecord(DeviceIds[i % DeviceIds.Count], TaskState.Done, null, 100)])
        {
            Steps = [new TaskStepInfo(0, "Do it", TaskStepState.Done, null, 100, start.AddSeconds(i), start.AddSeconds(i + 1))],
        }).ToList();
        await Host.Get<EfTaskStore>().AddRangeAsync(tasks, CancellationToken.None);
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class ServerScaleTests(ScaleServerFixture fixture, ITestOutputHelper output) : IClassFixture<ScaleServerFixture>
{
    private TestServerHost Host => fixture.Host;

    [Fact]
    public async Task ListTaskPluginsFor100000DevicesAndTenPluginsIsFastCachedAndCompact()
    {
        Proto.TaskPluginList compact = null!;
        await ServerScale.MeasureAsync(output, $"ListTaskPlugins compact, {ServerScale.Devices:N0} devices x 10 plugins, first call (CanRun {10 * ServerScale.Devices:N0} times)", TimeSpan.FromSeconds(10), async () =>
            compact = await Host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
        await ServerScale.MeasureAsync(output, "ListTaskPlugins compact again (cached)", TimeSpan.FromSeconds(2), async () =>
            compact = await Host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
        output.WriteLine($"Reply size: compact {compact.CalculateSize() / 1024} KB");
        Assert.True(compact.CalculateSize() < ServerScale.Devices / 5_000 * 200 * 1024, "compact reply below 200 KB per 5,000 devices");

        // The legacy full form (every runnable id of every plugin, about 35 MB at 100,000 devices) may exceed the client
        // limit of 32 MB; the client asks for the compact form.
        try
        {
            var full = await Host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
            output.WriteLine($"Legacy full ListTaskPlugins message: {full.CalculateSize() / 1024 / 1024.0:F1} MB in one message");
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.ResourceExhausted)
        {
            output.WriteLine("Legacy full ListTaskPlugins message: larger than the client limit of 32 MB (the client asks for the compact form)");
        }

        // The stream the client uses: one message per plugin, so even the full form (every runnable id) stays far below
        // the client's 32 MB limit per message, whatever the whole list adds up to.
        long total = 0;
        int largest = 0;
        using (var call = Host.Tasks.StreamTaskPlugins(new Proto.ListTaskPluginsRequest()))
        {
            await foreach (var plugin in call.ResponseStream.ReadAllAsync())
            {
                total += plugin.CalculateSize();
                largest = Math.Max(largest, plugin.CalculateSize());
            }
        }

        output.WriteLine($"StreamTaskPlugins full form: {total / 1024 / 1024.0:F1} MB in all, largest message {largest / 1024 / 1024.0:F1} MB");
        Assert.True(largest < 8 * 1024 * 1024, "one plugin's message stays below 8 MB at the device limit");

        var compactPlugin = compact.Plugins.Single(p => p.Id == "perf.task0");
        Assert.True(compactPlugin.RunnableOnAllExcept);
        Assert.Empty(compactPlugin.RunnableDeviceIds);

        // Not runnable: every 20th device (no API) and every 50th (unreachable); every 100th is both.
        var notRunnable = compactPlugin.NotRunnableDeviceIds.ToHashSet();
        Assert.Equal((ServerScale.Devices / 20) + (ServerScale.Devices / 50) - (ServerScale.Devices / 100), notRunnable.Count);
        Assert.All(fixture.DeviceIds.Select((id, i) => (id, i)), d => Assert.Equal(d.i % 20 == 0 || d.i % 50 == 0, notRunnable.Contains(d.id.ToString())));
        Assert.True(Host.Get<TaskPluginRunnableCache>().Computations <= 2, "computed once per device table version");
    }

    [Fact]
    public async Task DeviceWatchSnapshotOf100000DevicesEndsWithTheMarker()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var count = 0;
        await ServerScale.MeasureAsync(output, "Device Watch snapshot of 100,000 devices (100 APIs each) until SNAPSHOT_END", TimeSpan.FromSeconds(30), async () =>
        {
            using var call = Host.Devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, cancellationToken: cts.Token);
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                if (call.ResponseStream.Current.Kind == Proto.DeviceChanged.Types.Kind.SnapshotEnd)
                {
                    break;
                }

                count++;
            }
        });
        Assert.Equal(ServerScale.Devices, count);

        // The legacy one-message List (137 MB at 30,000 devices, about 460 MB at 100,000) may exceed the client limit of 32 MB; the Watch stream sends one device per message.
        try
        {
            var list = await Host.Devices.ListAsync(new Proto.Empty(), cancellationToken: cts.Token);
            output.WriteLine($"Legacy DeviceService.List message: {list.CalculateSize() / 1024 / 1024.0:F1} MB in one message");
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.ResourceExhausted)
        {
            output.WriteLine("Legacy DeviceService.List message: larger than the client limit of 32 MB (the Watch stream sends one device per message)");
        }
    }

    [Fact]
    public async Task TaskWatchWithASnapshotLimitSendsTheNewestTenThousandOfFiftyThousand()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var count = 0;
        await ServerScale.MeasureAsync(output, "Task Watch snapshot, limit 10,000 of 50,000, until SNAPSHOT_END", TimeSpan.FromSeconds(30), async () =>
        {
            using var call = Host.Tasks.Watch(new Proto.WatchTasksRequest { SnapshotLimit = 10_000, SnapshotEndMarker = true }, cancellationToken: cts.Token);
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                if (call.ResponseStream.Current.Kind == Proto.TaskChanged.Types.Kind.SnapshotEnd)
                {
                    break;
                }

                count++;
            }
        });
        Assert.Equal(10_000, count);

        Proto.TaskList page = null!;
        await ServerScale.MeasureAsync(output, "Task List page (100 at offset 25,000)", TimeSpan.FromSeconds(3), async () =>
            page = await Host.Tasks.ListAsync(new Proto.ListTasksRequest { Limit = 100, Offset = 25_000 }, cancellationToken: cts.Token));
        Assert.Equal(100, page.Tasks.Count);
        Assert.True(page.TotalCount >= ServerScale.Tasks);
    }
}

/// <summary>Not-runnable reasons in ListTaskPlugins for 100,000 devices x 15 plugins (own server: other plugins).</summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class ServerReasonScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReasonsFor100000DevicesAnd15PluginsKeepTheCompactReplySmallAndSendEveryIdOnce()
    {
        await using var host = await TestServerHost.StartAsync();
        var ids = await ServerScale.SeedDevicesAsync(host);
        ServerScale.RegisterPlugins(host, 15, lastApi: "api-999"); // the last one runs on no device

        Proto.TaskPluginList compact = null!;
        await ServerScale.MeasureAsync(output, $"ListTaskPlugins compact, {ServerScale.Devices:N0} devices x 15 plugins, first call (CanRun {15 * ServerScale.Devices:N0} times + reasons)", TimeSpan.FromSeconds(10), async () =>
            compact = await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
        await ServerScale.MeasureAsync(output, "ListTaskPlugins compact again (cached)", TimeSpan.FromSeconds(2), async () =>
            await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
        var reasonBytes = compact.Plugins.Where(p => p.RunnableOnAllExcept).Sum(p => p.NotRunnableGroups.Sum(g => g.CalculateSize() + 2));
        output.WriteLine($"Reply size: compact {compact.CalculateSize() / 1024.0:F1} KB (reasons of the 14 all-except plugins {reasonBytes} bytes)");
        Assert.True(compact.CalculateSize() < ServerScale.Devices / 5_000 * 200 * 1024, "compact reply below 200 KB per 5,000 devices");
        Assert.True(reasonBytes < 4 * 1024, "the reasons add only a few short strings, no device ids, in the compact form");

        foreach (var plugin in compact.Plugins)
        {
            var notRunnable = ServerScale.Devices - (plugin.RunnableOnAllExcept ? ServerScale.Devices - plugin.NotRunnableDeviceIds.Count : plugin.RunnableDeviceIds.Count);
            Assert.Equal(notRunnable, plugin.NotRunnableGroups.Sum(g => g.Count));
            var listed = plugin.NotRunnableGroups.SelectMany(g => g.DeviceIds).ToList();
            if (plugin.RunnableOnAllExcept)
            {
                Assert.Empty(listed); // the ids are in not_runnable_device_ids, sorted by group
                Assert.DoesNotContain(plugin.NotRunnableGroups, g => g.OtherDevices);
            }
            else
            {
                Assert.Single(plugin.NotRunnableGroups, g => g.OtherDevices);
                Assert.Equal(listed.Count, listed.Distinct().Count());
                Assert.DoesNotContain(listed, plugin.RunnableDeviceIds.Contains);
            }
        }

        // perf.task0 (api-000): every 50th unreachable, every 20th without the API (every 100th is both: unreachable wins).
        var task0 = compact.Plugins.Single(p => p.Id == "perf.task0");
        Assert.True(task0.RunnableOnAllExcept);
        Assert.Equal(
            [("Needs the API api-000 (this device has 12.11.77)", (ServerScale.Devices / 20) - (ServerScale.Devices / 100)), ("The device does not answer", ServerScale.Devices / 50)],
            task0.NotRunnableGroups.Select(g => (g.Reason, g.Count)).ToArray());
        var unreachable = ids.Where((_, i) => i % 50 == 0).Select(id => id.ToString()).ToHashSet();
        Assert.True(task0.NotRunnableDeviceIds.Skip((ServerScale.Devices / 20) - (ServerScale.Devices / 100)).All(unreachable.Contains));

        // perf.task14 (api-999): runs nowhere, so the runnable list is sent and the most common reason covers the rest.
        var none = compact.Plugins.Single(p => p.Id == "perf.task14");
        Assert.False(none.RunnableOnAllExcept);
        Assert.Empty(none.RunnableDeviceIds);
        var other = Assert.Single(none.NotRunnableGroups, g => g.OtherDevices);
        Assert.Equal(("Needs the API api-999 (this device has 12.11.77)", ServerScale.Devices - (ServerScale.Devices / 50), 0), (other.Reason, other.Count, other.DeviceIds.Count));
        Assert.Equal(ServerScale.Devices / 50, none.NotRunnableGroups.Single(g => !g.OtherDevices).DeviceIds.Count);
    }
}

/// <summary>Writes on 100,000 devices through gRPC: Run, credentials, Delete all, Remove (own server, it deletes).</summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class ServerBatchScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunSetCredentialsDeleteAllAndRemoveOn100000Devices()
    {
        await using var host = await TestServerHost.StartAsync();
        var ids = await ServerScale.SeedDevicesAsync(host);
        ServerScale.RegisterPlugins(host);
        var deviceIds = ids.Select(i => i.ToString()).ToList();

        await ServerScale.MeasureAsync(output, "SetCredentials on 100,000 devices (one transaction)", TimeSpan.FromSeconds(30), async () =>
        {
            var request = new Proto.SetCredentialsRequest { UserName = "root", Password = "pw" };
            request.DeviceIds.AddRange(deviceIds);
            await host.Devices.SetCredentialsAsync(request);
        });

        Proto.RunTaskReply reply = null!;
        await ServerScale.MeasureAsync(output, "Run on 100,000 devices (gRPC reply after one store transaction; measured about 28 s)", TimeSpan.FromSeconds(240), async () =>
        {
            var request = new Proto.RunTaskRequest { PluginId = "perf.task1" };
            request.DeviceIds.AddRange(deviceIds);
            reply = await host.Tasks.RunAsync(request);
        });
        Assert.Equal(ServerScale.Devices, reply.TaskIds.Count);

        var engine = host.Get<TaskEngine>();
        await ServerScale.MeasureAsync(output, "100,000 tasks finished (each with a full refresh after it; measured about 490 s)", TimeSpan.FromSeconds(900), async () =>
        {
            foreach (var id in reply.TaskIds)
            {
                await engine.WaitForCompletionAsync(Guid.Parse(id), CancellationToken.None);
            }
        });

        Proto.DeleteAllReply deleted = null!;
        await ServerScale.MeasureAsync(output, "DeleteAll 100,000 tasks", TimeSpan.FromSeconds(30), async () =>
            deleted = await host.Tasks.DeleteAllAsync(new Proto.Empty()));
        Assert.Equal(ServerScale.Devices, deleted.Deleted);

        await ServerScale.MeasureAsync(output, "Remove 100,000 devices (one transaction; measured 28 to 44 s)", TimeSpan.FromSeconds(240), async () =>
        {
            var request = new Proto.DeviceIds();
            request.Ids.AddRange(deviceIds);
            await host.Devices.RemoveAsync(request);
        });
        Assert.Empty(await host.Get<DeviceRepository>().ListDeviceIdsAsync(CancellationToken.None));
    }
}
