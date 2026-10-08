using System.IO.Compression;
using System.Runtime.CompilerServices;

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
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>
/// The server report of the dev camera through the real server in-process (temp data folder, the plugin registered as a
/// core plugin, calls over gRPC PluginService.Invoke). Read-only for the camera: full refresh reads and one
/// serverreport.cgi GET. The bundle goes to OADM_SCREENSHOT_DIR (or the temp folder) for inspection.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class SystemReportHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task System_report_of_the_dev_camera_through_the_server()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-system-report-hw", Guid.NewGuid().ToString("N"));
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
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new SystemReportPlugin(), PluginOrigin.FromAssembly(typeof(SystemReportPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var token = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services);
            var invoker = Grpc.Core.Interceptors.ChannelExtensions.Intercept(channel, new Oadm.Contracts.Security.AuthHeaderInterceptor(() => token));
            var plugins = new Proto.PluginService.PluginServiceClient(invoker);
            Assert.Contains((await plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == SystemReportPluginInfo.PluginId && p.NoPage);

            var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Device
            {
                Serial = "B8A44F631339",
                Address = Camera.Address,
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Scheme = Camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            }, CancellationToken.None);
            await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, Camera.User, Camera.Password, CancellationToken.None);
            var refreshed = await app.Services.GetRequiredService<DevicePollingService>().RefreshAsync(device.Id, CancellationToken.None);
            Console.WriteLine($"device: {refreshed?.Model} {refreshed?.FirmwareVersion} {refreshed?.Status}");

            async Task<string> Invoke(string method, object payload) =>
                (await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = SystemReportPluginInfo.PluginId, Method = method, PayloadJson = SystemReportJson.Serialize(payload) })).PayloadJson;

            var started = DateTime.UtcNow;
            var status = SystemReportJson.Deserialize<JobStatus>(await Invoke(SystemReportMethods.Start, new StartRequest { DeviceIds = [device.Id] }));
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (status.State is JobStates.Running or JobStates.Packing && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                status = SystemReportJson.Deserialize<JobStatus>(await Invoke(SystemReportMethods.Status, new StatusRequest { JobId = status.JobId }));
            }

            var single = status.Devices.Single();
            Console.WriteLine($"report: {single.State} {single.FileName} {single.Size} bytes in {(DateTime.UtcNow - started).TotalSeconds:F1} s {single.Error}");
            Assert.True(status.State == JobStates.Done, status.Error);
            Assert.Equal(DeviceReportStates.Done, single.State);

            using var bundle = new MemoryStream();
            ReportChunk chunk;
            do
            {
                chunk = SystemReportJson.Deserialize<ReportChunk>(await Invoke(SystemReportMethods.Read, new ReadRequest { JobId = status.JobId, Offset = bundle.Length }));
                bundle.Write(Convert.FromBase64String(chunk.DataBase64));
            }
            while (!chunk.Eof);

            var bytes = bundle.ToArray();
            using (var zip = new ZipArchive(new MemoryStream(bytes)))
            {
                Assert.Equal([single.FileName!, "summary.txt"], zip.Entries.Select(e => e.FullName).ToArray());
                using var inner = new ZipArchive(zip.Entries[0].Open());
                var names = inner.Entries.Select(e => e.FullName).ToList();
                Console.WriteLine("inside: " + string.Join(", ", names));
                Assert.Contains("serverreport_cgi.txt", names);
            }

            var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "system-report-10.0.0.48.zip");
            await File.WriteAllBytesAsync(path, bytes);
            Console.WriteLine($"bundle: {bytes.Length} bytes -> {path}");
            await Invoke(SystemReportMethods.Delete, new JobRequest { JobId = status.JobId });
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
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
