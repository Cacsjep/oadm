using System.Diagnostics;

using Oadm.Core.Tasks;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// Where the server stops scaling. Opt-in: <c>OADM_LIMITS_DEVICES</c> = the device counts to try,
/// e.g. "5000,25000,50000". Nothing is asserted on time: the test writes what it measured.
/// </summary>
[Trait("Category", "Perf")]
public sealed class ServerLimitsTests(ITestOutputHelper output)
{
    private static IReadOnlyList<int> Sizes =>
        (Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();

    [Fact]
    public async Task GrpcCallsAtEverySize()
    {
        foreach (var size in Sizes)
        {
            output.WriteLine($"== {size:N0} devices");
            await using var host = await TestServerHost.StartAsync();
            List<Guid> ids = [];
            await TimeAsync("Seed (100 APIs each)", async () => ids = await ServerScale.SeedDevicesAsync(host, size));
            ServerScale.RegisterPlugins(host);
            var deviceIds = ids.Select(i => i.ToString()).ToList();

            long bytes = 0;
            var count = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await TimeAsync("Device Watch snapshot until SNAPSHOT_END", async () =>
            {
                using var call = host.Devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, cancellationToken: cts.Token);
                while (await call.ResponseStream.MoveNext(cts.Token))
                {
                    if (call.ResponseStream.Current.Kind == Proto.DeviceChanged.Types.Kind.SnapshotEnd)
                    {
                        break;
                    }

                    bytes += call.ResponseStream.Current.CalculateSize();
                    count++;
                }
            });
            output.WriteLine($"  Snapshot: {count:N0} messages, {bytes / 1024.0 / 1024.0:N1} MB before gzip");
            output.WriteLine($"  Test process after the snapshot: heap {HeapMb():N0} MB, working set {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:N0} MB");

            Proto.TaskPluginList compact = null!;
            await TimeAsync("ListTaskPlugins compact, 10 plugins, first call", async () => compact = await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
            await TimeAsync("ListTaskPlugins compact (cached)", async () => compact = await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }));
            output.WriteLine($"  ListTaskPlugins compact reply: {compact.CalculateSize() / 1024.0:N0} KB (client limit 32 MB)");
            try
            {
                var full = await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
                output.WriteLine($"  ListTaskPlugins full reply (legacy): {full.CalculateSize() / 1024.0 / 1024.0:N1} MB");
            }
            catch (Grpc.Core.RpcException ex)
            {
                output.WriteLine($"  ListTaskPlugins full reply (legacy) refused by the test channel (4 MB): {ex.Status.StatusCode}");
            }

            await TimeAsync("SetDeviceTags, every device x 3 tags, one call", async () =>
            {
                var request = new Proto.SetDeviceTagsRequest { Add = { "Building A", "PTZ", "Outdoor" } };
                request.DeviceIds.AddRange(deviceIds);
                await host.Tags.SetDeviceTagsAsync(request, cancellationToken: cts.Token);
            });

            var runRequest = new Proto.RunTaskRequest { PluginId = "perf.task1" };
            runRequest.DeviceIds.AddRange(deviceIds);
            output.WriteLine($"  Run request: {runRequest.CalculateSize() / 1024.0 / 1024.0:N1} MB (server limit 16 MB)");
            Proto.RunTaskReply reply = null!;
            await TimeAsync("Run on every device (reply)", async () => reply = await host.Tasks.RunAsync(runRequest, cancellationToken: cts.Token));
            var engine = host.Get<TaskEngine>();
            await TimeAsync("Every task finished (trivial plugin)", async () =>
            {
                foreach (var id in reply.TaskIds)
                {
                    await engine.WaitForCompletionAsync(Guid.Parse(id), cts.Token);
                }
            });
            output.WriteLine($"  Test process after the run: heap {HeapMb():N0} MB, working set {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:N0} MB");
            await TimeAsync("DeleteAll tasks", async () => await host.Tasks.DeleteAllAsync(new Proto.Empty(), cancellationToken: cts.Token));

            var legacy = 0L;
            try
            {
                legacy = (await host.Devices.ListAsync(new Proto.Empty(), cancellationToken: cts.Token)).CalculateSize();
            }
            catch (Grpc.Core.RpcException ex)
            {
                output.WriteLine($"  Legacy DeviceService.List refused by the test channel: {ex.Status.StatusCode}");
            }

            output.WriteLine($"  Legacy DeviceService.List (unused by the client): {legacy / 1024.0 / 1024.0:N0} MB in one message");
            await TimeAsync("Remove every device (one call)", async () =>
            {
                var request = new Proto.DeviceIds();
                request.Ids.AddRange(deviceIds);
                await host.Devices.RemoveAsync(request, cancellationToken: cts.Token);
            });
        }
    }

    private async Task TimeAsync(string what, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        output.WriteLine($"  {what}: {watch.Elapsed.TotalMilliseconds:N0} ms");
    }

    private static double HeapMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
    }
}
