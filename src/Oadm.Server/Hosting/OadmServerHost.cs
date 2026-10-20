using System.Reflection;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Events;
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
using Oadm.Server.Events;
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
                // Capped (never fills the disk): a new file per day or at 20 MB, files of the last 14 days, at most 30
                // files (at most about 600 MB).
                .WriteTo.File(
                    Path.Combine(paths.LogsDirectory, "oadm-server-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 20L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    retainedFileTimeLimit: TimeSpan.FromDays(14),
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
        app.MapGrpcService<RoleGrpcService>();
        app.MapGrpcService<AuditGrpcService>();
        app.MapGrpcService<DeviceGrpcService>();
        app.MapGrpcService<TagGrpcService>();
        app.MapGrpcService<WaitingDeviceGrpcService>();
        app.MapGrpcService<TaskGrpcService>();
        app.MapGrpcService<SettingsGrpcService>();
        app.MapGrpcService<PluginGrpcService>();
        app.MapGrpcService<DiscoveryGrpcService>();
        app.MapGrpcService<AddDevicesGrpcService>();
        app.MapGrpcService<FileGrpcService>();
        app.MapGrpcService<LiveViewGrpcService>();
        app.MapGrpcService<EventGrpcService>();
        app.MapGrpcService<Compliance.ComplianceGrpcService>();
        app.MapGrpcService<Oadm.Server.DeviceWeb.DeviceWebGrpcService>();
        app.MapGrpcService<Backup.BackupGrpcService>();
        app.MapGrpcService<Logging.ServerLogGrpcService>();
        app.MapGrpcService<Sites.SiteGrpcService>();
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
        services.AddSingleton<CallerStreams>();
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

        // Sites (docs/sites.md): the site identity, the site listener (Accept site nodes) and the node sessions. The manager
        // is the real ISiteStates and ISiteTransport; without sites everything connects on the server's own network.
        services.AddSingleton(new Sites.SiteOptions());
        services.AddSingleton<Sites.SiteIdentityStore>();
        services.AddSingleton(sp => new Sites.SiteManager(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            sp.GetRequiredService<ServerSettingsStore>(),
            sp.GetRequiredService<Sites.SiteIdentityStore>(),
            sp.GetRequiredService<Core.Auth.AuditLog>(),
            sp.GetRequiredService<Sites.SiteOptions>(),
            sp.GetRequiredService<Sites.SiteOptions>().Time ?? sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Sites.SiteManager>>(),
            sp.GetService<Sdk.Network.IFirewallRules>())); // the Windows service: the site port's rule while Accept site nodes is on
        services.RemoveAll<Core.Sites.ISiteStates>();
        services.AddSingleton<Core.Sites.ISiteStates>(sp => sp.GetRequiredService<Sites.SiteManager>());
        services.AddSingleton<Core.Sites.ISiteTransport>(sp => sp.GetRequiredService<Sites.SiteManager>());
        services.AddSingleton<Core.Sites.ISiteRelays>(sp => sp.GetRequiredService<Sites.SiteManager>());
        services.AddSingleton<Core.Sites.ISiteNetwork>(sp => sp.GetRequiredService<Sites.SiteManager>());
        // The network checks of plugins (IDeviceNetwork) on every route: the server's own network or a site's node.
        services.AddSingleton(sp => new Core.Sites.DeviceNetworks(sp.GetService<Core.Sites.ISiteNetwork>(), sp.GetService<Core.Sites.ISiteStates>()));
        services.AddSingleton<Sites.SiteAdmin>();

        // VAPIX
        // Extra trust anchors of core plugins (PKI CA): device certificates chaining to them are rated Trusted.
        services.AddSingleton<TrustAnchorRegistry>();
        // Routing (docs/sites.md): every device connection opens on its route; without a site transport only the server's
        // own network exists and everything connects as before.
        services.AddSingleton(sp => new Core.Sites.DeviceRouter(sp.GetService<Core.Sites.ISiteTransport>(), sp.GetService<Core.Sites.ISiteStates>()));
        services.AddSingleton(sp => new Core.Sites.RoutedDns(sp.GetService<Core.Sites.ISiteStates>(), sp.GetService<Core.Sites.ISiteNetwork>()));
        services.AddSingleton<IVapixConnector>(sp => new VapixConnector(sp.GetRequiredService<TrustAnchorRegistry>(), sp.GetRequiredService<Core.Sites.DeviceRouter>()));
        services.AddSingleton<VapixClientFactory>();
        services.AddSingleton<Sdk.Vapix.IVapixClientFactory>(sp => sp.GetRequiredService<VapixClientFactory>());
        services.AddSingleton(sp => new VapixProbe(TimeSpan.FromSeconds(3), router: sp.GetRequiredService<Core.Sites.DeviceRouter>()));

        // Uploads (task dialogs: firmware, ACAP packages)
        services.AddSingleton(sp => new Core.Uploads.UploadStore(
            sp.GetRequiredService<OadmPaths>(),
            sp.GetRequiredService<ServerSettingsStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Core.Uploads.UploadStore>>()));
        services.AddSingleton<Sdk.Plugins.IUploadedFiles>(sp => sp.GetRequiredService<Core.Uploads.UploadStore>());
        services.AddHostedService<UploadCleanupHostedService>();
        services.AddHostedService<TaskRetentionHostedService>();

        // Backup and restore (Settings page): automatic backups, restore staged and applied by the next start.
        services.AddSingleton(sp => new Core.Backup.BackupArchive(
            sp.GetRequiredService<OadmPaths>(), sp.GetRequiredService<ILogger<Core.Backup.BackupArchive>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Backup.IServerRestart, Backup.ServerRestart>();
        services.AddSingleton(sp => new Backup.BackupManager(
            sp.GetRequiredService<OadmPaths>(),
            sp.GetRequiredService<Core.Backup.BackupArchive>(),
            sp.GetRequiredService<ServerSettingsStore>(),
            new Core.Security.PluginSecretProtector(sp.GetRequiredService<Core.Security.CredentialProtector>()),
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<TaskEngine>(),
            sp.GetRequiredService<Backup.IServerRestart>(),
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            sp.GetRequiredService<Core.Auth.AuditLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Backup.BackupManager>>()));
        services.AddHostedService<Backup.BackupScheduleHostedService>();
        // Live view: RTSP upstreams shared between viewers
        services.AddSingleton<ILiveVideoSourceFactory, AxisLiveVideoSourceFactory>();
        services.AddSingleton<IDeviceOptics, AxisDeviceOptics>();
        services.AddSingleton<IDevicePtz, DevicePtz>();
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
            return new TaskEngineOptions
            {
                MaxParallelTasksPerPluginSource = () => parallelism.Current,
                Networks = sp.GetRequiredService<Core.Sites.DeviceNetworks>(),
            };
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
        services.AddSingleton<DeviceLoginService>();
        services.AddSingleton<DeviceFirstPasswordService>();
        services.AddSingleton(sp => new TaskPluginQueries(
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>())
        {
            Networks = sp.GetRequiredService<Core.Sites.DeviceNetworks>(),
        });
        services.AddSingleton<Sdk.Tasks.ITaskRunner>(sp => sp.GetRequiredService<TaskEngine>());
        services.AddSingleton<TaskPluginRunnableCache>();
        services.AddSingleton<PluginActivation>();
        services.AddSingleton<PluginLicenses>();
        services.AddHostedService<PluginLicenseRefreshHostedService>();
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
            firewall: sp.GetService<Sdk.Network.IFirewallRules>(),
            pluginDataRoot: sp.GetRequiredService<OadmPaths>().PluginDataDirectory,
            autoAdd: sp.GetRequiredService<Sdk.Devices.IDeviceAutoAdd>(),
            eventPublishers: id => sp.GetRequiredService<RuleEngine>().PublisherFor(id),
            eventSubscriptions: id => sp.GetRequiredService<RuleEngine>().SubscriptionsFor(id),
            networks: () => new Core.Sites.PluginNetworkScope(
                sp.GetRequiredService<Core.Sites.DeviceNetworks>(),
                sp.GetService<Core.Sites.ISiteRelays>(),
                sp.GetService<Sdk.Network.IFirewallRules>(),
                sp.GetRequiredService<TimeProvider>())));

        // Compliance checks (host and plugin types); their events go to the event rules.
        services.AddSingleton<Core.Compliance.ComplianceCatalog>();
        services.AddSingleton(sp => new Core.Compliance.ComplianceStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new Core.Compliance.ComplianceEngineOptions());
        services.AddSingleton(sp => new Core.Compliance.ComplianceEngine(
            sp.GetRequiredService<Core.Compliance.ComplianceCatalog>(),
            sp.GetRequiredService<Core.Compliance.ComplianceStore>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<IDeviceChangeFeed>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Core.Compliance.ComplianceEngineOptions>(),
            sp.GetRequiredService<ILogger<Core.Compliance.ComplianceEngine>>(),
            () => sp.GetRequiredService<RuleEngine>().PublisherFor(Core.Compliance.ComplianceEngine.EventSourceId)));
        services.AddSingleton<IHostEventSource>(sp => sp.GetRequiredService<Core.Compliance.ComplianceEngine>());
        // Sites: "Site offline: <site>" and "Any site offline" for the event rules; devices of a site published when it
        // goes offline or online (computed status, nothing written).
        services.AddSingleton(sp => new SiteEventSource(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            sp.GetRequiredService<Core.Sites.ISiteStates>(),
            () => sp.GetRequiredService<RuleEngine>().PublisherFor(SiteEventSource.EventSourceId),
            sp.GetRequiredService<ILogger<SiteEventSource>>()));
        services.AddSingleton<IHostEventSource>(sp => sp.GetRequiredService<SiteEventSource>());
        services.AddSingleton(sp => new Core.Sites.SiteStatusPublisher(
            sp.GetRequiredService<Core.Sites.ISiteStates>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<IDeviceChangeFeed>(),
            ids => sp.GetRequiredService<DevicePollingService>().QueueRefresh(ids),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Core.Sites.SiteStatusPublisher>>()));
        services.AddSingleton(sp => new Core.Compliance.ComplianceService(
            sp.GetRequiredService<Core.Compliance.ComplianceStore>(),
            sp.GetRequiredService<Core.Compliance.ComplianceEngine>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // Event rules: IF events THEN actions (built-in device status events, plugin events and actions).
        services.AddSingleton<EventCatalog>();
        services.AddSingleton(sp => new RuleStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            new Core.Security.PluginSecretProtector(sp.GetRequiredService<Core.Security.CredentialProtector>()),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new EventLog(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OadmDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<EventLog>>()));
        services.AddSingleton(sp => new ActionRunner(
            sp.GetRequiredService<EventCatalog>(),
            sp.GetRequiredService<RuleStore>(),
            sp.GetRequiredService<EventLog>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<IPluginSettingsProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(new RuleEngineOptions());
        services.AddSingleton(sp => new RuleEngine(
            sp.GetRequiredService<EventCatalog>(),
            sp.GetRequiredService<RuleStore>(),
            sp.GetRequiredService<EventLog>(),
            sp.GetRequiredService<ActionRunner>(),
            sp.GetRequiredService<DeviceRepository>(),
            sp.GetRequiredService<IDeviceChangeFeed>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<RuleEngineOptions>(),
            sp.GetRequiredService<ILogger<RuleEngine>>()));
        services.AddSingleton<EventRulesService>();
        services.AddHostedService<EventLogRetentionHostedService>();

        // Polling
        services.AddSingleton<DevicePollingService>();
        services.AddHostedService<DevicePollingHostedService>();
        // Re-find managed devices that moved (mDNS, matched by serial)
        services.AddSingleton(new DeviceRelocationOptions());
        services.AddHostedService<DeviceRelocationHostedService>();

        // Discovery
        services.AddSingleton<IMdnsBrowser, MdnsBrowser>();
        services.AddSingleton<VapixDeviceProbe>(sp => new VapixDeviceProbe(logger: sp.GetService<ILogger<VapixDeviceProbe>>(), router: sp.GetRequiredService<Core.Sites.DeviceRouter>()));
        services.AddSingleton<IDeviceProbe>(sp => sp.GetRequiredService<VapixDeviceProbe>());
        services.AddSingleton<RangeScanner>();
        services.AddSingleton<IDnsNameLookup>(sp => new SystemDnsNameLookup(sp.GetRequiredService<Core.Sites.RoutedDns>()));
        services.AddSingleton(sp => new CoreDiscoveryService(
            sp.GetRequiredService<IMdnsBrowser>(),
            sp.GetRequiredService<RangeScanner>(),
            sp.GetRequiredService<IDeviceProbe>(),
            sp.GetService<ILogger<CoreDiscoveryService>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IDnsNameLookup>(),
            sp.GetService<Core.Sites.ISiteNetwork>()));
        services.AddSingleton<IDiscoveryBackend, CoreDiscoveryBackend>();
        services.AddSingleton<DiscoveryAuthenticator>();
        // Adding devices: the add page's Commit and the automatic add of core plugins (DHCP server) share DeviceAdder.
        services.AddSingleton<DeviceAdder>();
        services.AddSingleton<DeviceAutoAddService>();
        services.AddSingleton<Sdk.Devices.IDeviceAutoAdd>(sp => sp.GetRequiredService<DeviceAutoAddService>());
        // The import's waiting list: devices that did not answer, tried again and added once they do.
        services.AddSingleton<WaitingDeviceRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<WaitingDeviceRunner>());
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

        // 0b. A restore staged before the restart: its database, key and plugin data replace the server's (the replaced files
        // are kept in restore/previous-*), before anything opens them. The server's own TLS key moves to the restored key.
        var backupArchive = sp.GetRequiredService<Core.Backup.BackupArchive>();
        backupArchive.CleanTemp();
        backupArchive.ApplyPending((oldKey, newKey) => ServerTlsCertificate.ReencryptKey(Path.Combine(paths.DataDirectory, ServerTlsCertificate.FileName), oldKey, newKey, logger));

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

        // A plugin straight from its build output (Oadm:PluginDir, plugin development): loaded first, so it wins over an
        // installed copy of the same package. Never as a service: a service loads only admin-only folders.
        foreach (var directory in PluginDirectories(app.Configuration))
        {
            if (sp.GetRequiredService<ServerRunMode>().IsService)
            {
                LogPluginDirIgnored(logger, directory);
            }
            else if (loader.LoadPackage(directory) is not null)
            {
                LogPluginDirLoaded(logger, directory);
            }
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

        // 2b. Roles: every permission the loaded plugins declare (on or off); a role gets the default of a new one once.
        var hadUsers = await sp.GetRequiredService<Core.Auth.UserStore>().HasUsersAsync(ct).ConfigureAwait(false);
        var catalog = Core.Auth.PermissionCatalog.Build(registry);
        var sync = await sp.GetRequiredService<Core.Auth.RoleStore>().SyncCatalogAsync(catalog, ct).ConfigureAwait(false);
        await Core.Auth.RolesUpgradeNotice.RecordAsync(sp.GetRequiredService<ServerSettingsStore>(), sync, hadUsers, catalog, sp.GetRequiredService<TimeProvider>(), ct).ConfigureAwait(false);

        // 2c. A restore this start applied: its report (Settings page) and audit entry; every login session ends.
        sp.GetRequiredService<Backup.BackupManager>().RemovePartialFiles();
        await sp.GetRequiredService<Backup.BackupManager>().RecordRestoreAsync(ct).ConfigureAwait(false);

        // 2d. Sites: the identity and the bound sites, each "starting" until its node settles (before the event rules and
        // the polling read site states); the site listener opens when Accept site nodes is on.
        await sp.GetRequiredService<Sites.SiteManager>().LoadRemovedAsync(ct).ConfigureAwait(false); // removed sites with kept devices
        sp.GetRequiredService<Sites.SiteManager>().WatchDevices(sp.GetRequiredService<IDeviceChangeFeed>()); // the page's device counts
        await sp.GetRequiredService<Sites.SiteManager>().StartAsync(ct).ConfigureAwait(false);

        // 3. Tasks left running by a previous process (the parallel task limit is loaded first).
        await sp.GetRequiredService<TaskParallelismSetting>().LoadAsync(ct).ConfigureAwait(false);
        var engine = sp.GetRequiredService<TaskEngine>();
        await engine.RecoverInterruptedAsync(ct).ConfigureAwait(false);

        // 4. Core plugins of the packages that are on (Settings page, Plugins; manifest default otherwise).
        await sp.GetRequiredService<PluginActivation>().LoadAsync(ct).ConfigureAwait(false);

        // 4a. Licenses of commercial plugins: the server ID, the entered licenses, trials; before any plugin runs.
        await sp.GetRequiredService<PluginLicenses>().InitializeAsync(ct).ConfigureAwait(false);
        sp.GetRequiredService<ActionRunner>().Licenses = sp.GetRequiredService<PluginLicenses>();

        // 4b. Event rules before the core plugins: their publishers queue into a running engine, and each event type
        // waits for its source's first full report before a rule that uses it runs anything.
        var compliance = sp.GetRequiredService<Core.Compliance.ComplianceEngine>();
        await compliance.LoadAsync(ct).ConfigureAwait(false);
        var siteEvents = sp.GetRequiredService<SiteEventSource>();
        await siteEvents.LoadAsync(ct).ConfigureAwait(false);
        var rules = sp.GetRequiredService<RuleEngine>();
        await rules.StartAsync(ct).ConfigureAwait(false);
        registry.EnabledChanged += (_, _) => rules.SourcesChanged();
        compliance.EventTypesChanged += (_, _) => rules.SourcesChanged();
        siteEvents.EventTypesChanged += (_, _) => rules.SourcesChanged();
        siteEvents.Start();
        sp.GetRequiredService<Core.Sites.SiteStatusPublisher>().Start();
        await sp.GetRequiredService<CorePluginHost>().StartAllAsync(ct).ConfigureAwait(false);
        // 4c. Compliance checks after the core plugins: the plugins' check types read devices through them.
        await compliance.StartAsync(ct).ConfigureAwait(false);
        // Rules are evaluated at once, but their actions wait until here: contributed actions (Run VAPIX command) need
        // their core plugin running.
        sp.GetRequiredService<ActionRunner>().PluginsStarted();

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
        await sp.GetRequiredService<Core.Compliance.ComplianceEngine>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<Core.Sites.SiteStatusPublisher>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<SiteEventSource>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<CorePluginHost>().StopAllAsync(ct).ConfigureAwait(false);
        await sp.GetRequiredService<RuleEngine>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<ActionRunner>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<EventLog>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<TaskEngine>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<CoreDiscoveryService>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<LiveViewHub>().DisposeAsync().ConfigureAwait(false);
        await sp.GetRequiredService<Sites.SiteManager>().DisposeAsync().ConfigureAwait(false);
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
    /// <param name="appDirectory">The app folder; default <see cref="AppContext.BaseDirectory"/> (tests pass a folder inside the repository).</param>
    public static IReadOnlyList<string> DefaultPluginRoots(OadmPaths paths, bool includeDevelopment = true, string? appDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = new List<string> { PluginPaths.Bundled(appDirectory), PluginPaths.Installed(paths.DataDirectory) };
        if (includeDevelopment && PluginPaths.Development(appDirectory) is { } development)
        {
            roots.Add(development);
        }

        return roots;
    }

    /// <summary>The configuration key of plugin folders loaded straight from a build output (several separated by ";").</summary>
    public const string PluginDirKey = "Oadm:PluginDir";

    /// <summary>The folders of <see cref="PluginDirKey"/> (<c>--Oadm:PluginDir=&lt;folder&gt;</c>), full paths, in order.</summary>
    public static IReadOnlyList<string> PluginDirectories(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return [.. (configuration[PluginDirKey] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin loaded from the development folder {Directory}; it wins over an installed copy")]
    private static partial void LogPluginDirLoaded(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Oadm:PluginDir {Directory} is ignored: the server runs as a service, which loads only its own plugin folders")]
    private static partial void LogPluginDirIgnored(ILogger logger, string directory);

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
