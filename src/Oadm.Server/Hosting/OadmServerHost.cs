using System.Reflection;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.LiveView;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Server.AddDevices;
using Oadm.Server.Auth;
using Oadm.Server.Devices;
using Oadm.Server.Discovery;
using Oadm.Server.Files;
using Oadm.Server.LiveView;
using Oadm.Server.Plugins;
using Oadm.Server.Settings;
using Oadm.Server.Tasks;

using Serilog;
using Serilog.Events;

using ILogger = Microsoft.Extensions.Logging.ILogger;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;

namespace Oadm.Server.Hosting;

/// <summary>Options for <see cref="OadmServerHost.Build"/>; tests use them to isolate the server.</summary>
public sealed class OadmServerHostOptions
{
    /// <summary>Data folder. Null: configuration key <c>Oadm:DataDir</c>, then env OADM_DATA_DIR, then LocalApplicationData/Oadm.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Overrides <c>Server.ListenUrl</c> from the settings (also configuration key <c>Oadm:ListenUrl</c>).</summary>
    public string? ListenUrl { get; init; }

    /// <summary>Plugin roots to scan. Null: installed (data folder) plus the repo's artifacts/plugins.</summary>
    public IReadOnlyList<string>? PluginRoots { get; init; }

    /// <summary>Runs after the default registrations; tests replace services here.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Runs on the builder before Build (tests: UseTestServer).</summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    /// <summary>Writes logs to the console (off in tests).</summary>
    public bool LogToConsole { get; init; } = true;

    /// <summary>
    /// Runs as the installed service: admin-only folder checks, Windows firewall rules for plugins, no development
    /// plugin folder. Null: detected (<see cref="ServiceHosting.IsRunningAsService"/>), so tests and console runs are off.
    /// </summary>
    public bool? ServiceMode { get; init; }
}

/// <summary>Whether this server runs as the installed service (see <see cref="OadmServerHostOptions.ServiceMode"/>).</summary>
public sealed record ServerRunMode(bool IsService);

/// <summary>
/// Composition root of the OADM server. Startup order: database (and master key), plugins, task
/// recovery, core plugins, then the background services and Kestrel (gRPC over h2c). Shutdown
/// runs in reverse: Kestrel and background services, core plugins, task engine, database.
/// </summary>
public static partial class OadmServerHost
{
    public static WebApplication Build(string[] args, OadmServerHostOptions? options = null)
    {
        options ??= new OadmServerHostOptions();
        var builder = WebApplication.CreateBuilder(ServiceHosting.CreateBuilderOptions(args, ServiceHosting.IsRunningAsService()));
        ServiceHosting.AddServiceLifetimes(builder.Services);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2);
            // https listen URLs use the server's own certificate (server-tls.json), loaded in StartAsync.
            k.ConfigureHttpsDefaults(h => h.ServerCertificateSelector = (_, _) => k.ApplicationServices.GetRequiredService<ServerTlsCertificate>().Certificate);
        });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Services.AddSerilog((sp, lc) =>
        {
            var paths = sp.GetRequiredService<OadmPaths>();
            lc.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning) // never log auth headers
                .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
                .ReadFrom.Configuration(sp.GetRequiredService<IConfiguration>())
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(paths.LogsDirectory, "oadm-server-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
            if (options.LogToConsole)
            {
                lc.WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            }
        });

        AddOadmServer(builder.Services, builder.Configuration, options);
        options.ConfigureBuilder?.Invoke(builder);
        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapGrpcService<AuthGrpcService>();
        app.MapGrpcService<UserGrpcService>();
        app.MapGrpcService<AuditGrpcService>();
        app.MapGrpcService<DeviceGrpcService>();
        app.MapGrpcService<TaskGrpcService>();
        app.MapGrpcService<SettingsGrpcService>();
        app.MapGrpcService<PluginGrpcService>();
        app.MapGrpcService<DiscoveryGrpcService>();
        app.MapGrpcService<AddDevicesGrpcService>();
        app.MapGrpcService<FileGrpcService>();
        app.MapGrpcService<LiveViewGrpcService>();
        if (app.Environment.IsDevelopment())
        {
            app.MapGrpcReflectionService();
        }

        return app;
    }

    /// <summary>All server registrations (without the gRPC endpoint mapping).</summary>
    public static IServiceCollection AddOadmServer(IServiceCollection services, IConfiguration configuration, OadmServerHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        services.AddGrpc(o =>
        {
            o.EnableDetailedErrors = false;

            // Scale: Watch snapshots and ListTaskPlugins for 5,000 devices are large and repetitive (API
            // lists, ids); gzip shrinks them several times. Only used when the client accepts gzip
            // (Grpc.Net.Client does by default).
            o.ResponseCompressionAlgorithm = "gzip";
            o.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Fastest;

            // A Run or Remove on 5,000+ devices carries 5,000 ids (about 200 KB); keep headroom.
            o.MaxReceiveMessageSize = 16 * 1024 * 1024;

            // Every call needs a login (except AuthService Status, Login, CreateFirstAdmin); roles per AccessPolicy.
            o.Interceptors.Add<AuthInterceptor>();
        });
        services.AddGrpcReflection();

        services.AddOadmPersistence(_ => new OadmPaths(options.DataDirectory ?? configuration["Oadm:DataDir"]));
        services.AddSingleton(options);

        // Access: TLS certificate of the gRPC endpoint, audit and session cleanup
        services.AddSingleton<ServerTlsCertificate>();
        services.AddHostedService<AuthMaintenanceHostedService>();

        // Installed service (SYSTEM / root): admin-only folders, firewall rules of the NTP and DHCP plugins (Windows).
        var serviceMode = options.ServiceMode ?? ServiceHosting.IsRunningAsService();
        services.AddSingleton(new ServerRunMode(serviceMode));
        if (serviceMode)
        {
            services.AddSingleton(_ => ServiceHosting.CreateFolderPermissions());
            services.AddSingleton<FolderGuard>();
            if (OperatingSystem.IsWindows())
            {
                services.AddSingleton<Sdk.Network.IFirewallRules>(_ => new NetshFirewallRules(new ProcessCommandRunner(), Sdk.Network.HostOsInfo.ExecutablePath()));
            }
        }

        // VAPIX
        // Extra trust anchors of core plugins (PKI CA): device certificates chaining to them are rated Trusted.
        services.AddSingleton<TrustAnchorRegistry>();
        services.AddSingleton<IVapixConnector>(sp => new VapixConnector(sp.GetRequiredService<TrustAnchorRegistry>()));
        services.AddSingleton<VapixClientFactory>();
        services.AddSingleton<Sdk.Vapix.IVapixClientFactory>(sp => sp.GetRequiredService<VapixClientFactory>());
        services.AddSingleton(_ => new VapixProbe(TimeSpan.FromSeconds(3)));

        // Uploads (task dialogs: firmware, ACAP packages)
        services.AddSingleton(sp => new Core.Uploads.UploadStore(
            sp.GetRequiredService<OadmPaths>(),
            sp.GetRequiredService<ServerSettingsStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Core.Uploads.UploadStore>>()));
        services.AddSingleton<Sdk.Plugins.IUploadedFiles>(sp => sp.GetRequiredService<Core.Uploads.UploadStore>());
        services.AddHostedService<UploadCleanupHostedService>();
        services.AddHostedService<TaskRetentionHostedService>();
        // Live view: RTSP upstreams shared between viewers
        services.AddSingleton<ILiveVideoSourceFactory, AxisLiveVideoSourceFactory>();
        services.AddSingleton(new LiveViewHubOptions());
        services.AddSingleton<LiveViewHub>();
        // Device event streams (RTSP metadata) for core plugins such as the Metadata Monitor
        services.AddSingleton<Sdk.Devices.IDeviceEventStreams, DeviceEventStreams>();

        // Plugins and tasks
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<PluginLoader>();
        // Tasks.MaxParallelPerPlugin (server setting, default 16, 1..256, Settings page): read live by the engine,
        // a change applies to tasks that start afterwards. A plugin's MaxParallelDevices can only lower it
        // (firmware: 4). The former configuration value Oadm:MaxParallelTasksPerPlugin is no longer read.
        services.AddSingleton(sp => new TaskParallelismSetting(sp.GetRequiredService<ServerSettingsStore>()));
        services.AddSingleton(sp =>
        {
            var parallelism = sp.GetRequiredService<TaskParallelismSetting>();
            return new TaskEngineOptions { MaxParallelTasksPerPluginSource = () => parallelism.Current };
        });
        services.AddSingleton(sp =>
        {
            var engine = new TaskEngine(
                sp.GetRequiredService<ITaskStore>(),
                sp.GetRequiredService<PluginRegistry>(),
                sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
                sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<TaskEngineOptions>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Sdk.Plugins.IUploadedFiles>(),
                sp.GetRequiredService<ITaskDeviceCredentials>(),
                sp.GetRequiredService<ITaskDeviceAddresses>());
            // A raised limit starts queued tasks at once; a lowered one lets running tasks finish.
            sp.GetRequiredService<TaskParallelismSetting>().Changed += (_, _) => engine.RescheduleQueued();
            return engine;
        });
        services.AddSingleton<ITaskDeviceCredentials, TaskDeviceCredentials>();
        services.AddSingleton<DeviceAddressService>();
        services.AddSingleton<ITaskDeviceAddresses>(sp => sp.GetRequiredService<DeviceAddressService>());
        services.AddSingleton(sp => new TaskPluginQueries(
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<Sdk.Tasks.ITaskRunner>(sp => sp.GetRequiredService<TaskEngine>());
        services.AddSingleton<TaskPluginRunnableCache>();
        services.AddSingleton(sp => new CorePluginHost(
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<Sdk.Tasks.ITaskRunner>(),
            sp.GetRequiredService<IPluginSettingsProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            new Core.Security.PluginSecretProtector(sp.GetRequiredService<Core.Security.CredentialProtector>()),
            trustAnchors: sp.GetRequiredService<TrustAnchorRegistry>(),
            eventStreams: sp.GetRequiredService<Sdk.Devices.IDeviceEventStreams>(),
            firewall: sp.GetService<Sdk.Network.IFirewallRules>()));

        // Polling
        services.AddSingleton<DevicePollingService>();
        services.AddHostedService<DevicePollingHostedService>();
        // Re-find managed devices that moved (mDNS, matched by serial)
        services.AddSingleton(new DeviceRelocationOptions());
        services.AddHostedService<DeviceRelocationHostedService>();

        // Discovery
        services.AddSingleton<IMdnsBrowser, MdnsBrowser>();
        services.AddSingleton<VapixDeviceProbe>(sp => new VapixDeviceProbe(logger: sp.GetService<ILogger<VapixDeviceProbe>>()));
        services.AddSingleton<IDeviceProbe>(sp => sp.GetRequiredService<VapixDeviceProbe>());
        services.AddSingleton<RangeScanner>();
        services.AddSingleton(sp => new CoreDiscoveryService(
            sp.GetRequiredService<IMdnsBrowser>(),
            sp.GetRequiredService<RangeScanner>(),
            sp.GetRequiredService<IDeviceProbe>(),
            sp.GetService<ILogger<CoreDiscoveryService>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDiscoveryBackend, CoreDiscoveryBackend>();
        services.AddSingleton<DiscoveryAuthenticator>();
        return services;
    }

    /// <summary>Runs the startup steps, then starts background services and Kestrel.</summary>
    public static async Task StartAsync(WebApplication app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var sp = app.Services;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Oadm.Server");
        var options = sp.GetRequiredService<OadmServerHostOptions>();
        var paths = sp.GetRequiredService<OadmPaths>();
        LogStarting(logger, typeof(OadmServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?", paths.DataDirectory);

        // 0. Installed service: the data and extraction folders must be admin-only (fixed, else the server does not start).
        var folderGuard = sp.GetService<FolderGuard>();
        if (folderGuard is not null)
        {
            await folderGuard.EnsureServerFoldersAsync(ServiceHosting.ServerFolders(paths.DataDirectory), ct).ConfigureAwait(false);
        }

        // 1. Database and master key.
        await sp.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct).ConfigureAwait(false);

        // 1b. No user yet: a one-time setup code for creating the first administrator from another computer.
        await sp.GetRequiredService<Core.Auth.AuthManager>().PrepareFirstAdminAsync(ct).ConfigureAwait(false);

        // 2. Plugins: built-in first (their ids are reserved), then installed and development folders.
        var registry = sp.GetRequiredService<PluginRegistry>();
        // As a service every plugin folder must be admin-only, and the repository's artifacts/plugins is never used.
        var loader = sp.GetRequiredService<PluginLoader>();
        var roots = options.PluginRoots ?? DefaultPluginRoots(paths, includeDevelopment: !sp.GetRequiredService<ServerRunMode>().IsService);
        if (folderGuard is not null)
        {
            var refused = await folderGuard.CheckPluginFoldersAsync(roots, ct).ConfigureAwait(false);
            loader.FolderCheck = folder => refused.GetValueOrDefault(Path.GetFullPath(folder));
        }

        loader.LoadFromRoots(roots);
        foreach (var plugin in registry.TaskPlugins)
        {
            LogTaskPlugin(logger, plugin.Id, plugin.Origin.Directory ?? "built-in");
        }

        foreach (var error in registry.Errors)
        {
            LogPluginError(logger, error.Source, error.Message);
        }

        // 3. Tasks left running by a previous process (the parallel task limit is loaded first).
        await sp.GetRequiredService<TaskParallelismSetting>().LoadAsync(ct).ConfigureAwait(false);
        var engine = sp.GetRequiredService<TaskEngine>();
        await engine.RecoverInterruptedAsync(ct).ConfigureAwait(false);

        // 4. Core plugins.
        await sp.GetRequiredService<CorePluginHost>().StartAllAsync(ct).ConfigureAwait(false);

        // 5. Background services and Kestrel.
        var listenUrl = options.ListenUrl
            ?? app.Configuration["Oadm:ListenUrl"]
            ?? (await sp.GetRequiredService<ServerSettingsStore>().GetServerSettingsAsync(ct).ConfigureAwait(false)).ListenUrl;
        var addresses = sp.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null && addresses.Count == 0 && string.IsNullOrEmpty(app.Configuration["urls"]))
        {
            addresses.Add(listenUrl);
        }

        if (addresses is not null && addresses.Any(a => a.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
        {
            var regenerate = string.Equals(app.Configuration[ServerTlsCertificate.RegenerateConfigKey], "true", StringComparison.OrdinalIgnoreCase);
            await sp.GetRequiredService<ServerTlsCertificate>().LoadOrCreateAsync(regenerate, ct).ConfigureAwait(false);
        }
        else if (addresses is { Count: > 0 })
        {
            LogPlainHttp(logger, string.Join(", ", addresses));
        }

        await app.StartAsync(ct).ConfigureAwait(false);
        LogListening(logger, addresses is null ? "(in-process test server)" : string.Join(", ", addresses));
    }

    /// <summary>Stops Kestrel and background services, then core plugins, then the task engine.</summary>
    public static async Task StopAsync(WebApplication app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var sp = app.Services;
        await app.StopAsync(ct).ConfigureAwait(false);
        await sp.GetRequiredService<CorePluginHost>().StopAllAsync(ct).ConfigureAwait(false);
        await sp.GetRequiredService<TaskEngine>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<CoreDiscoveryService>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<LiveViewHub>().DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Start, wait for Ctrl+C / SIGTERM, stop.</summary>
    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await StartAsync(app).ConfigureAwait(false);
        await app.WaitForShutdownAsync().ConfigureAwait(false);
        await StopAsync(app).ConfigureAwait(false);
    }

    /// <summary>Bundled (app folder), installed (data folder) and, unless running as a service, the repository's artifacts/plugins.</summary>
    public static IReadOnlyList<string> DefaultPluginRoots(OadmPaths paths, bool includeDevelopment = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = new List<string> { PluginPaths.Bundled(), PluginPaths.Installed(paths.DataDirectory) };
        if (includeDevelopment && PluginPaths.Development() is { } development)
        {
            roots.Add(development);
        }

        return roots;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM server {Version} starting, data folder {DataDirectory}")]
    private static partial void LogStarting(ILogger logger, string version, string dataDirectory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task plugin {PluginId} available ({Origin})")]
    private static partial void LogTaskPlugin(ILogger logger, string pluginId, string origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin problem in {Source}: {Message}")]
    private static partial void LogPluginError(ILogger logger, string source, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TLS is off ({Urls}): logins and data travel unencrypted. Use an https:// listen URL except for tests.")]
    private static partial void LogPlainHttp(ILogger logger, string urls);

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC listening on {Urls}")]
    private static partial void LogListening(ILogger logger, string urls);
}
