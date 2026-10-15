using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Oadm.Core.Tasks;
using Oadm.Server.Devices;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>Where the per-task cost of a run on every device comes from (opt-in, OADM_LIMITS_DEVICES).</summary>
[Trait("Category", "Perf")]
public sealed class TaskRunLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PerTaskCostWithAndWithoutTheRefreshAfterATask()
    {
        var sizes = (Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        foreach (var size in sizes)
        {
            foreach (var refresh in (Environment.GetEnvironmentVariable("OADM_LIMITS_REFRESH") == "on" ? new[] { true } : new[] { true, false }))
            {
                await using var host = await TestServerHost.StartAsync();
                var ids = await ServerScale.SeedDevicesAsync(host, size);
                ServerScale.RegisterPlugins(host);
                if (!refresh)
                {
                    await host.App.Services.GetServices<IHostedService>().OfType<DevicePollingHostedService>().Single().StopAsync(CancellationToken.None);
                }

                var request = new Proto.RunTaskRequest { PluginId = "perf.task1" };
                request.DeviceIds.AddRange(ids.Select(i => i.ToString()));
                var watch = Stopwatch.StartNew();
                var reply = await host.Tasks.RunAsync(request);
                var engine = host.Get<TaskEngine>();
                foreach (var id in reply.TaskIds)
                {
                    await engine.WaitForCompletionAsync(Guid.Parse(id), CancellationToken.None);
                }

                output.WriteLine($"{size:N0} devices, refresh after each task {(refresh ? "on " : "off")}: {watch.Elapsed.TotalSeconds:N1} s, {watch.Elapsed.TotalMilliseconds / size:N2} ms per task");
            }
        }
    }
}
