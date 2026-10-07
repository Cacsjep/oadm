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

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>
/// Snapshots of the dev camera through the real server in-process (temp data folder, the plugin registered as a
/// core plugin, calls over gRPC PluginService.Invoke). Read-only for the camera: full refresh reads, param.cgi
/// reads and image.cgi snapshots only. The PDF goes to OADM_SCREENSHOT_DIR (or the temp folder) for inspection.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class SnapshotHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task Snapshot_and_report_from_the_dev_camera_through_the_server()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-snapshot-hw", Guid.NewGuid().ToString("N"));
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
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new SnapshotReportPlugin(), PluginOrigin.FromAssembly(typeof(SnapshotReportPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var plugins = new Proto.PluginService.PluginServiceClient(channel);
            Assert.Contains((await plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == SnapshotReportPluginInfo.PluginId);

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
            Console.WriteLine($"device: {refreshed?.Model} {refreshed?.FirmwareVersion} {refreshed?.Status} cert {refreshed?.CertTrust} until {refreshed?.CertNotAfterUtc:yyyy-MM-dd}");

            async Task<string> Invoke(string method, object payload) =>
                (await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = SnapshotReportPluginInfo.PluginId, Method = method, PayloadJson = SnapshotReportJson.Serialize(payload) })).PayloadJson;

            var tiles = SnapshotReportJson.Deserialize<ListSourcesResult>(await Invoke(SnapshotReportMethods.ListSources, new ListSourcesRequest())).Tiles;
            foreach (var t in tiles)
            {
                Console.WriteLine($"tile: {t.Title} camera={t.Camera} label={t.SourceLabel} error={t.Error}");
            }

            Assert.NotEmpty(tiles);
            Assert.All(tiles, t => Assert.Null(t.Error));

            var started = DateTime.UtcNow;
            var snapshot = SnapshotReportJson.Deserialize<SnapshotResult>(await Invoke(SnapshotReportMethods.Snapshot, new SnapshotRequest { DeviceId = device.Id, Camera = tiles[0].Camera }));
            Console.WriteLine($"snapshot: {snapshot.Width}x{snapshot.Height}, {snapshot.JpegBase64?.Length * 3 / 4} bytes in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            Assert.Null(snapshot.Error);
            var jpeg = Convert.FromBase64String(snapshot.JpegBase64!);
            Assert.True(SnapshotRequests.TryGetJpegSize(jpeg, out var width, out var height));
            Assert.Equal((1280, 720), (width, height));
            Assert.Equal((width, height), (snapshot.Width, snapshot.Height));
            Assert.NotNull(snapshot.CapturedUtc);

            var missing = SnapshotReportJson.Deserialize<SnapshotResult>(await Invoke(SnapshotReportMethods.Snapshot, new SnapshotRequest { DeviceId = device.Id, Camera = 9 }));
            Assert.Equal("The device has no video source 9", missing.Error);

            var request = new ReportRequest
            {
                Site = "OADM lab",
                Technician = "Hardware test",
                Date = DateOnly.FromDateTime(DateTime.Today),
                Items = [.. tiles.Select(t => new ReportItem { DeviceId = t.Device.DeviceId, Camera = t.Camera })],
            };
            var status = SnapshotReportJson.Deserialize<ReportJobStatus>(await Invoke(SnapshotReportMethods.GenerateReport, request));
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (status.State == ReportJobStates.Running && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                status = SnapshotReportJson.Deserialize<ReportJobStatus>(await Invoke(SnapshotReportMethods.ReportStatus, new ReportJobRequest { JobId = status.JobId }));
            }

            Assert.True(status.State == ReportJobStates.Done, status.Error);
            Assert.Equal(0, status.Failed);

            using var pdf = new MemoryStream();
            ReportChunk chunk;
            do
            {
                chunk = SnapshotReportJson.Deserialize<ReportChunk>(await Invoke(SnapshotReportMethods.ReadReport, new ReadReportRequest { JobId = status.JobId, Offset = pdf.Length }));
                pdf.Write(Convert.FromBase64String(chunk.DataBase64));
            }
            while (!chunk.Eof);

            var bytes = pdf.ToArray();
            var inspector = PdfInspector.Open(bytes);
            Assert.Equal(1 + ((tiles.Count + 1) / 2), inspector.PageCount);
            Assert.Equal(tiles.Count, inspector.Images().Count);
            Assert.Contains("OADM lab", inspector.PageText(0), StringComparison.Ordinal);
            Assert.Contains("10.0.0.48", inspector.PageText(1), StringComparison.Ordinal);

            var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "snapshot-report-10.0.0.48.pdf");
            await File.WriteAllBytesAsync(path, bytes);
            Console.WriteLine($"report: {inspector.PageCount} pages, {bytes.Length} bytes -> {path}");
            await Invoke(SnapshotReportMethods.DeleteReport, new ReportJobRequest { JobId = status.JobId });
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
