using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Vapix;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Support;

/// <summary>
/// The real server (all services, real SQLite in a temp data folder) on an in-process TestServer,
/// with VAPIX and discovery wired to a <see cref="FakeAxisNetwork"/>. No plugin folders are scanned.
/// </summary>
internal sealed class TestServerHost : IAsyncDisposable
{
    private TestServerHost(WebApplication app, GrpcChannel channel, string dataDirectory, FakeAxisNetwork network)
    {
        App = app;
        Channel = channel;
        DataDirectory = dataDirectory;
        Network = network;
        Devices = new Proto.DeviceService.DeviceServiceClient(channel);
        Tasks = new Proto.TaskService.TaskServiceClient(channel);
        Settings = new Proto.SettingsService.SettingsServiceClient(channel);
        Plugins = new Proto.PluginService.PluginServiceClient(channel);
        Discovery = new Proto.DiscoveryService.DiscoveryServiceClient(channel);
        AddDevices = new Proto.AddDevicesService.AddDevicesServiceClient(channel);
        LiveView = new Proto.LiveViewService.LiveViewServiceClient(channel);
    }

    public WebApplication App { get; }

    public GrpcChannel Channel { get; }

    public string DataDirectory { get; }

    public FakeAxisNetwork Network { get; }

    public IServiceProvider Services => App.Services;

    public Proto.DeviceService.DeviceServiceClient Devices { get; }

    public Proto.TaskService.TaskServiceClient Tasks { get; }

    public Proto.SettingsService.SettingsServiceClient Settings { get; }

    public Proto.PluginService.PluginServiceClient Plugins { get; }

    public Proto.DiscoveryService.DiscoveryServiceClient Discovery { get; }

    public Proto.AddDevicesService.AddDevicesServiceClient AddDevices { get; }

    public Proto.LiveViewService.LiveViewServiceClient LiveView { get; }

    public T Get<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static string NewDataDirectory() => Path.Combine(Path.GetTempPath(), "oadm-server-tests", Guid.NewGuid().ToString("N"));

    /// <param name="network">Fake devices; null for an empty network.</param>
    /// <param name="useRealNetwork">Keep the real VAPIX connector and probes (hardware tests).</param>
    /// <param name="configureServices">Extra replacements after the defaults (e.g. a fake live video source).</param>
    public static async Task<TestServerHost> StartAsync(FakeAxisNetwork? network = null, bool useRealNetwork = false, Action<IServiceCollection>? configureServices = null)
    {
        network ??= new FakeAxisNetwork();
        var dataDirectory = NewDataDirectory();
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
                if (!useRealNetwork)
                {
                    services.RemoveAll<IVapixConnector>();
                    services.AddSingleton(network.CreateConnector());
                    services.RemoveAll<VapixProbe>();
                    services.AddSingleton(network.CreateProbe());
                    services.RemoveAll<VapixDeviceProbe>();
                    services.AddSingleton(_ => new VapixDeviceProbe(network.CreateHandler(), TimeSpan.FromSeconds(2)));
                }

                configureServices?.Invoke(services);
            },
        });

        await OadmServerHost.StartAsync(app);
        var server = app.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        return new TestServerHost(app, channel, dataDirectory, network);
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await OadmServerHost.StopAsync(App);
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the rolling log file may still be closing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>mDNS browser that never finds anything (no multicast in unit tests).</summary>
    private sealed class SilentMdnsBrowser : IMdnsBrowser
    {
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(
            MdnsBrowseOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
