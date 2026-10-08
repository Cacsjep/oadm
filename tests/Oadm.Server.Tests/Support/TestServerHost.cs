using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Security;
using Oadm.Core.Auth;
using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Vapix;
using Oadm.Server.Auth;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Support;

/// <summary>
/// The real server (all services, real SQLite in a temp data folder) on an in-process TestServer,
/// with VAPIX and discovery wired to a <see cref="FakeAxisNetwork"/>. No plugin folders are scanned.
/// Every client calls as the logged-in administrator "admin" (<see cref="Invoker"/>); <see cref="InvokerFor"/> gives
/// other users or no login. Password hashing uses few iterations to keep the tests fast.
/// </summary>
internal sealed class TestServerHost : IAsyncDisposable
{
    public const string AdminUserName = "admin";

    private TestServerHost(WebApplication app, GrpcChannel channel, string adminToken, string dataDirectory, FakeAxisNetwork network)
    {
        App = app;
        Channel = channel;
        AdminToken = adminToken;
        Invoker = InvokerFor(adminToken);
        DataDirectory = dataDirectory;
        Network = network;
        Devices = new Proto.DeviceService.DeviceServiceClient(Invoker);
        Tasks = new Proto.TaskService.TaskServiceClient(Invoker);
        Settings = new Proto.SettingsService.SettingsServiceClient(Invoker);
        Plugins = new Proto.PluginService.PluginServiceClient(Invoker);
        Discovery = new Proto.DiscoveryService.DiscoveryServiceClient(Invoker);
        AddDevices = new Proto.AddDevicesService.AddDevicesServiceClient(Invoker);
        LiveView = new Proto.LiveViewService.LiveViewServiceClient(Invoker);
        Auth = new Proto.AuthService.AuthServiceClient(Invoker);
        Users = new Proto.UserService.UserServiceClient(Invoker);
        Audit = new Proto.AuditService.AuditServiceClient(Invoker);
    }

    public WebApplication App { get; }

    /// <summary>The raw channel: calls without a token (use <see cref="Invoker"/> for the administrator).</summary>
    public GrpcChannel Channel { get; }

    /// <summary>Calls as the administrator "admin".</summary>
    public CallInvoker Invoker { get; }

    public string AdminToken { get; }

    public Proto.AuthService.AuthServiceClient Auth { get; }

    public Proto.UserService.UserServiceClient Users { get; }

    public Proto.AuditService.AuditServiceClient Audit { get; }

    /// <summary>Calls with <paramref name="token"/> (null: without a login) and the client machine name "testpc".</summary>
    public CallInvoker InvokerFor(string? token) => Channel.Intercept(new AuthHeaderInterceptor(() => token, "testpc"));

    /// <summary>Creates (if needed) a user with the role and returns an invoker that calls as that user.</summary>
    public async Task<CallInvoker> InvokerForUserAsync(string userName, Oadm.Sdk.Plugins.UserRole role) =>
        InvokerFor(await InProcessAccess.CreateTokenAsync(App.Services, userName, role));

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
    /// <param name="createAdmin">False: no user exists (first-administrator tests); the clients call without a token.</param>
    public static async Task<TestServerHost> StartAsync(FakeAxisNetwork? network = null, bool useRealNetwork = false, Action<IServiceCollection>? configureServices = null, bool createAdmin = true)
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
                services.RemoveAll<PasswordHasher>();
                services.AddSingleton(new PasswordHasher(1_000));
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
        var token = createAdmin ? await InProcessAccess.CreateTokenAsync(app.Services, AdminUserName) : "";
        return new TestServerHost(app, channel, token, dataDirectory, network);
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
