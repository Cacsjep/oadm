using System.Runtime.CompilerServices;

using Avalonia.Headless;

using Grpc.Core;
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
using Oadm.Plugins.ImageHealth.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.ImageHealth.Tests;

/// <summary>
/// A camera with AXIS Image Health Analytics through the real server in-process (temp data folder, the plugin registered
/// as a core plugin) and the real page over gRPC: the page opens a session, the server asks the app for its status
/// (GET only), the five detections arrive (or, with the app stopped, the camera counts as without the app). Renders
/// <c>image-health-dashboard-live.png</c>. Read-only for the camera. Uses the dev camera whose note mentions "AIHA" or
/// "Image Health" (dev-cameras.yaml, <c>rtspPort</c> for a forwarded RTSP port); skipped without one.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class ImageHealthHardwareTests
{
    private static DevCamera? Camera => DevCameras.All.FirstOrDefault(c =>
        c.Note?.Contains("Image Health", StringComparison.OrdinalIgnoreCase) == true
        || c.Note?.Contains("AIHA", StringComparison.OrdinalIgnoreCase) == true
        || c.Note?.Contains("loan", StringComparison.OrdinalIgnoreCase) == true);

    [HardwareFact]
    public async Task The_page_shows_the_live_detections_of_a_camera_with_the_app()
    {
        var camera = Camera;
        if (camera is null)
        {
            return; // no AIHA camera configured
        }

        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-aiha-hw", Guid.NewGuid().ToString("N"));
        var plugin = new ImageHealthPlugin();
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
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(plugin, PluginOrigin.FromAssembly(typeof(ImageHealthPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var token = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services);
            var invoker = Grpc.Core.Interceptors.ChannelExtensions.Intercept(channel, new Oadm.Contracts.Security.AuthHeaderInterceptor(() => token));
            var plugins = new Proto.PluginService.PluginServiceClient(invoker);

            var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Device
            {
                Serial = "B8A44F000001",
                Address = camera.Address,
                Model = "Q3548-LVE",
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Scheme = camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            }, CancellationToken.None);
            await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
            var ctx = new GrpcPageContext(plugins);
            var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
            var (rows, _) = await session.Dispatch(async () =>
            {
                var vm = new ImageHealthViewModel(ctx);
                var view = new ImageHealthView { DataContext = vm };
                var window = Headless.Host(view);
                window.Show(); // attached: the page opens its session
                var until = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < until && (vm.IsChecking || vm.SummaryText.Length == 0 || (vm.Rows.Count == 1 && vm.Rows[0].IsChecking)))
                {
                    Headless.Pump();
                    await Task.Delay(100);
                }

                Headless.Pump();
                await Task.Delay(300);
                Headless.Pump();
                Headless.Capture(window, "image-health-dashboard-live.png");
                var shown = vm.Rows.ToList();
                foreach (var row in shown)
                {
                    Console.WriteLine($"{row.Address}: {row.AppText}, blur {row.Blur.Text}, block {row.Block.Text}, redirect {row.Redirect.Text}, under-exposure {row.UnderExposure.Text}, unsuitability {row.Unsuitability.Text}");
                }

                Console.WriteLine(vm.SummaryText);
                window.Close(); // detached: the page closes its session
                await Task.Delay(500);
                return (shown, vm.SummaryText);
            }, CancellationToken.None);

            // The app runs: one row with all five detections; stopped: one "Not running" row.
            var row = Assert.Single(rows);
            Assert.True(row.IsRunning || row.IsNotRunning, row.AppText);
            if (row.IsRunning)
            {
                Assert.True(row.Blur.HasValue && row.Block.HasValue && row.Redirect.HasValue && row.UnderExposure.HasValue && row.Unsuitability.HasValue);
            }

            Assert.False(plugin.Monitor!.IsChecking); // nothing runs after the check
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

    /// <summary>The page context of the client over gRPC PluginService (like the client's CorePluginClientContext).</summary>
    private sealed class GrpcPageContext(Proto.PluginService.PluginServiceClient plugins) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => [];

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            var reply = await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = ImageHealthPluginInfo.PluginId, Method = method, PayloadJson = payloadJson ?? string.Empty }, cancellationToken: ct);
            return string.IsNullOrEmpty(reply.PayloadJson) ? null : reply.PayloadJson;
        }

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var call = plugins.Watch(new Proto.WatchPluginRequest { PluginId = ImageHealthPluginInfo.PluginId }, cancellationToken: ct);
            while (await call.ResponseStream.MoveNext(ct))
            {
                yield return new PluginEvent(call.ResponseStream.Current.Topic, call.ResponseStream.Current.PayloadJson);
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
