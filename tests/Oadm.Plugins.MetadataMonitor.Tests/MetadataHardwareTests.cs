using System.Runtime.CompilerServices;

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
using Oadm.Sdk.Devices;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>
/// The event stream of the dev camera through the real server in-process (temp data folder, the plugin registered as
/// a core plugin, Start / Stop over gRPC PluginService.Invoke, messages over PluginService.Watch). Read-only for the
/// camera: RTSP DESCRIBE / SETUP / PLAY / TEARDOWN of the event stream only.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class MetadataHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task Start_receives_initialized_messages_from_the_dev_camera_and_stop_ends_the_stream()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-metadata-hw", Guid.NewGuid().ToString("N"));
        var plugin = new MetadataMonitorPlugin();
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
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(plugin, PluginOrigin.FromAssembly(typeof(MetadataMonitorPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var plugins = new Proto.PluginService.PluginServiceClient(channel);
            Assert.Contains((await plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == MetadataMonitorPluginInfo.PluginId);

            var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Device
            {
                Serial = "B8A44F631339",
                Address = Camera.Address,
                Model = "P3265-V",
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Scheme = Camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            }, CancellationToken.None);
            await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, Camera.User, Camera.Password, CancellationToken.None);

            using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var watch = plugins.Watch(new Proto.WatchPluginRequest { PluginId = MetadataMonitorPluginInfo.PluginId }, cancellationToken: watchCts.Token);
            await Wait.UntilAsync(() => app.Services.GetRequiredService<CorePluginHost>().Events.WatcherCount(MetadataMonitorPluginInfo.PluginId) == 1);

            async Task<string?> Invoke(string method, object payload) =>
                (await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = MetadataMonitorPluginInfo.PluginId, Method = method, PayloadJson = MetadataJson.Serialize(payload) })).PayloadJson;

            var started = DateTime.UtcNow;
            var reply = MetadataJson.Deserialize<StartReply>(await Invoke(MetadataMethods.Start, new StartRequest(device.Id)));
            Assert.Null(reply.Error);
            Assert.NotNull(reply.StreamId);

            var messages = new List<MetadataMessage>();
            while (await watch.ResponseStream.MoveNext(watchCts.Token))
            {
                var item = watch.ResponseStream.Current;
                if (item.Topic == MetadataMethods.MessagesTopic)
                {
                    var batch = MetadataJson.Deserialize<MessagesEvent>(item.PayloadJson);
                    Assert.Equal(reply.StreamId, batch.StreamId);
                    messages.AddRange(batch.Messages);
                    if (messages.Any(m => m.Operation == "Initialized"))
                    {
                        break;
                    }
                }
            }

            Console.WriteLine($"{messages.Count} messages, first after {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms; topics: {string.Join(", ", messages.Select(m => m.Topic).Distinct().Take(8))}");
            var initialized = messages.First(m => m.Operation == "Initialized");
            Assert.Equal(MessageCategories.Event, initialized.Category);
            Assert.StartsWith("[INIT]", initialized.Info, StringComparison.Ordinal);
            Assert.Equal(1, messages[0].Seq);
            Assert.Equal(1, plugin.StreamCount);

            await Invoke(MetadataMethods.Stop, new StreamRequest(reply.StreamId!));
            Assert.Equal(0, plugin.StreamCount);
            await watchCts.CancelAsync();
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
