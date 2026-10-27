using System.Globalization;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// A real check of the dev cameras named in <c>OADM_HW_CHECK_CAMERAS</c> (comma-separated addresses of dev-cameras.yaml)
/// through the real server in-process: plan, start with 1-minute phases, wait for the end, print the result. Writes the
/// exposure value of each camera during Low light and restores it, so it is HardwareWrite: run only when the developer
/// asks, never in <c>manage test hardware</c>. Skipped without the variable.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class LoadTestHardwareTests
{
    [HardwareFact]
    public async Task A_check_of_real_cameras_completes_and_restores_the_exposure()
    {
        var wanted = (Environment.GetEnvironmentVariable("OADM_HW_CHECK_CAMERAS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cameras = DevCameras.All.Where(c => wanted.Contains(c.Address)).ToList();
        if (cameras.Count == 0)
        {
            return; // nothing asked for
        }

        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-ic-hw", Guid.NewGuid().ToString("N"));
        var app = OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory,
            PluginRoots = [],
            LogToConsole = false,
            ConfigureBuilder = b => b.WebHost.UseTestServer(),
            ConfigureServices = services =>
            {
                services.RemoveAll<IMdnsBrowser>();
                services.AddSingleton<IMdnsBrowser, SilentMdnsBrowser>();
            },
        });
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new LoadTestPlugin(), PluginOrigin.FromAssembly(typeof(LoadTestPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var token = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services);
            var invoker = Grpc.Core.Interceptors.ChannelExtensions.Intercept(channel, new Oadm.Contracts.Security.AuthHeaderInterceptor(() => token));
            var plugins = new Proto.PluginService.PluginServiceClient(invoker);

            var ids = new List<Guid>();
            var serial = 0;
            foreach (var camera in cameras)
            {
                var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Device
                {
                    Serial = "B8A44F" + (++serial).ToString("X6", CultureInfo.InvariantCulture),
                    Address = camera.Address,
                    Model = camera.Note ?? "Camera",
                    Status = DeviceStatus.Ok,
                    Category = DeviceCategory.Camera,
                    Scheme = camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
                }, CancellationToken.None);
                await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
                ids.Add(device.Id);
            }

            async Task<T> Invoke<T>(string method, object payload)
            {
                var reply = await plugins.InvokeAsync(new Proto.InvokeRequest
                {
                    PluginId = LoadTestInfo.PluginId,
                    Method = method,
                    PayloadJson = LoadTestJson.Serialize(payload),
                });
                return LoadTestJson.Deserialize<T>(reply.PayloadJson);
            }

            var plan = await Invoke<CheckPlan>(LoadTestMethods.Plan, new PlanRequest(ids, 50, 1));
            Console.WriteLine($"Plan: {plan.Streams} streams, {plan.Batches} batches, {plan.Duration}, peak {plan.PeakMbps:0} Mbit/s, low light {plan.LowLightMbps:0} Mbit/s, refusal {plan.Refusal ?? "-"}");
            foreach (var left in plan.LeftOut)
            {
                Console.WriteLine($"  left out {left.Name}: {left.Reason}");
            }

            Assert.Null(plan.Refusal);
            Assert.True(plan.Streams > 0);

            var started = await Invoke<RunSummary>(LoadTestMethods.Start, new StartRequest(ids, 50, 1));
            var until = DateTime.UtcNow.AddMinutes(20);
            RunSummary summary = started;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var list = await Invoke<RunList>(LoadTestMethods.ListRuns, new { });
                summary = list.Runs.First(r => r.RunId == started.RunId);
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {summary.State} batch {summary.Batch}/{summary.Batches} {summary.Phase?.ToString() ?? "connecting"} {summary.Progress:P0}");
                if (summary.State != RunState.Running)
                {
                    break;
                }
            }

            var detail = await Invoke<RunDetail>(LoadTestMethods.GetRun, new RunRequest(started.RunId));
            var s = detail.Summary;
            Console.WriteLine($"Result: {s.State}, passed {s.Passed}, failed streams {s.FailedStreams}, peak {s.PeakMbps:0.0} Mbit/s, worst loss {s.WorstLossPercent:0.00} %, headroom {s.Headroom ?? "-"}, verdict \"{s.Verdict}\"");
            foreach (var warning in s.RestoreWarnings)
            {
                Console.WriteLine($"  restore warning: {warning}");
            }

            foreach (var row in detail.Streams)
            {
                Console.WriteLine($"{row.Address} cam {row.Camera} {row.SourceName} {row.Transport} {row.Stream}: packet loss {row.PacketLossPercent?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-"} %, frame loss {row.FrameLossPercent:0.00} %, {row.AverageMbps:0.0} Mbit/s, jitter {row.JitterMs:0.0} ms, problem {row.Problem ?? "-"}, note {row.Note ?? "-"}");
                foreach (var phase in row.Phases)
                {
                    Console.WriteLine($"    {phase.Phase}: {phase.Result} packet {phase.PacketLossPercent?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-"} % frame {phase.FrameLossPercent:0.00} % late {phase.LateFramePercent:0.00} % {phase.AverageMbps:0.0} Mbit/s jitter {phase.JitterMs:0.0} ms {phase.Problem}");
                }
            }

            Assert.Equal(RunState.Completed, s.State);
            Assert.Empty(s.RestoreWarnings);
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>No multicast from tests.</summary>
    private sealed class SilentMdnsBrowser : IMdnsBrowser
    {
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }
    }
}
