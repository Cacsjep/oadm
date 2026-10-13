using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Client.Api;
using Oadm.Core.Devices;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Plugins.EdgeRecordingExport.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Hosting;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>
/// A camera with recordings on its SD card through the real server in-process (temp data folder, the plugin registered as
/// a core plugin) and the real client API over gRPC (PluginService.Invoke and Download): the page searches the last two
/// minutes and downloads them into a temp folder. Read-only for the camera. Uses every dev camera (dev-cameras.yaml); a
/// camera without recordings in that time is only searched. The AXIS P3265-V of the development setup records 10 s clips
/// continuously.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class EdgeRecordingHardwareTests
{
    [HardwareFact]
    public async Task The_page_finds_and_downloads_the_latest_recordings_of_the_dev_cameras()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-edge-hw", Guid.NewGuid().ToString("N"));
        using var folder = new TempFolder();
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
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new EdgeRecordingExportPlugin(), PluginOrigin.FromAssembly(typeof(EdgeRecordingExportPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var devices = new List<IDeviceInfo>();
            foreach (var camera in DevCameras.All)
            {
                var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Device
                {
                    Serial = "B8A44F" + devices.Count.ToString("X6", CultureInfo.InvariantCulture),
                    Address = camera.Address,
                    Model = camera.Note ?? "Camera",
                    Status = DeviceStatus.Ok,
                    Category = DeviceCategory.Camera,
                    Scheme = camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
                }, CancellationToken.None);
                await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
                devices.Add(new TestDevice(camera.Address, camera.Note) { Id = device.Id, Serial = device.Serial });
            }

            var server = app.GetTestServer();
            using var api = new GrpcOadmApi("http://localhost", handler: server.CreateHandler())
            {
                AccessToken = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services),
            };
            using var vm = new EdgeRecordingViewModel(new GrpcPageContext(api, devices))
            {
                StartDate = DateTime.Today,
                StartTime = DateTime.Now.AddMinutes(-2).TimeOfDay,
                StopDate = DateTime.Today,
                StopTime = DateTime.Now.AddMinutes(1).TimeOfDay,
                PickFolder = () => Task.FromResult<string?>(folder.Path),
            };
            vm.SetChoice(DeviceChoice.AllDevices);

            await vm.SearchCommand.ExecuteAsync(null);
            Console.WriteLine(vm.SummaryText);
            foreach (var row in vm.Rows)
            {
                Console.WriteLine($"{row.Address}: {row.StatusText} {row.StatusDetail} {row.SizeText} {row.Result?.Format}");
            }

            Assert.Equal(devices.Count, vm.Rows.Count);
            var withRecordings = vm.Rows.Where(r => r.RecordingCount > 0).ToList();
            if (withRecordings.Count == 0)
            {
                return; // no camera recorded in the last two minutes
            }

            await vm.DownloadCommand.ExecuteAsync(null);
            Console.WriteLine(vm.SummaryText);
            foreach (var row in withRecordings)
            {
                Console.WriteLine($"{row.Address}: {row.StatusText} {row.StatusDetail}");
                var files = Directory.GetFiles(Path.Combine(folder.Path, RecordingFiles.CameraFolderName(row.Device)));
                Assert.Equal(row.RecordingCount, files.Length);
                foreach (var file in files)
                {
                    var head = new byte[8];
                    await using (var stream = File.OpenRead(file))
                    {
                        Assert.Equal(8, await stream.ReadAsync(head));
                    }

                    // MP4 (ftyp box) or Matroska (EBML header)
                    Assert.True(head.AsSpan(4, 4).SequenceEqual("ftyp"u8) || head.AsSpan(0, 4).SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]), Path.GetFileName(file));
                    Console.WriteLine($"  {Path.GetFileName(file)} {new FileInfo(file).Length:N0} bytes");
                }

                Assert.Equal(0, row.Failed);
            }

            var audit = await app.Services.GetRequiredService<Oadm.Core.Auth.AuditLog>().ListAsync(1000, 0, CancellationToken.None);
            Assert.Equal(withRecordings.Sum(r => r.RecordingCount), audit.Entries.Count(e => e.Detail?.StartsWith("Exported recording: ", StringComparison.Ordinal) == true));
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

    /// <summary>The page context of the client over the real gRPC client (like the client's CorePluginClientContext).</summary>
    private sealed class GrpcPageContext(GrpcOadmApi api, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => devices;

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(EdgeRecordingInfo.PluginId, method, payloadJson, ct);

        public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.DownloadCorePluginAsync(EdgeRecordingInfo.PluginId, method, payloadJson, ct);
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
