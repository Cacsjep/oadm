using System.Reflection;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Server.AddDevices;
using Oadm.Server.Devices;
using Oadm.Server.Discovery;
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
}

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
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http2));
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
        app.MapGrpcService<DeviceGrpcService>();
        app.MapGrpcService<TaskGrpcService>();
        app.MapGrpcService<SettingsGrpcService>();
        app.MapGrpcService<PluginGrpcService>();
        app.MapGrpcService<DiscoveryGrpcService>();
        app.MapGrpcService<AddDevicesGrpcService>();
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

        services.AddGrpc(o => o.EnableDetailedErrors = false);
        services.AddGrpcReflection();

        services.AddOadmPersistence(_ => new OadmPaths(options.DataDirectory ?? configuration["Oadm:DataDir"]));
        services.AddSingleton(options);

        // VAPIX
        services.AddSingleton<IVapixConnector, VapixConnector>();
        services.AddSingleton<VapixClientFactory>();
        services.AddSingleton<Sdk.Vapix.IVapixClientFactory>(sp => sp.GetRequiredService<VapixClientFactory>());
        services.AddSingleton(_ => new VapixProbe(TimeSpan.FromSeconds(3)));

        // Plugins and tasks
        services.AddSingleton<PluginRegistry>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton(new TaskEngineOptions());
        services.AddSingleton(sp => new TaskEngine(
            sp.GetRequiredService<ITaskStore>(),
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TaskEngineOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Sdk.Tasks.ITaskRunner>(sp => sp.GetRequiredService<TaskEngine>());
        services.AddSingleton(sp => new CorePluginHost(
            sp.GetRequiredService<PluginRegistry>(),
            sp.GetRequiredService<Sdk.Devices.IDeviceRepository>(),
            sp.GetRequiredService<Sdk.Vapix.IVapixClientFactory>(),
            sp.GetRequiredService<Sdk.Tasks.ITaskRunner>(),
            sp.GetRequiredService<IPluginSettingsProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<AddDevicesTaskPlugin>();

        // Polling
        services.AddSingleton<DevicePollingService>();
        services.AddHostedService<DevicePollingHostedService>();

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

        // 1. Database and master key.
        await sp.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct).ConfigureAwait(false);

        // 2. Plugins: built-in first (their ids are reserved), then installed and development folders.
        var registry = sp.GetRequiredService<PluginRegistry>();
        registry.RegisterTaskPlugin(sp.GetRequiredService<AddDevicesTaskPlugin>(), PluginOrigin.FromAssembly(typeof(OadmServerHost).Assembly));
        var roots = options.PluginRoots ?? DefaultPluginRoots(paths);
        sp.GetRequiredService<PluginLoader>().LoadFromRoots(roots);
        foreach (var plugin in registry.TaskPlugins)
        {
            LogTaskPlugin(logger, plugin.Id, plugin.Origin.Directory ?? "built-in");
        }

        foreach (var error in registry.Errors)
        {
            LogPluginError(logger, error.Source, error.Message);
        }

        // 3. Tasks left running by a previous process.
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
    }

    /// <summary>Start, wait for Ctrl+C / SIGTERM, stop.</summary>
    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await StartAsync(app).ConfigureAwait(false);
        await app.WaitForShutdownAsync().ConfigureAwait(false);
        await StopAsync(app).ConfigureAwait(false);
    }

    public static IReadOnlyList<string> DefaultPluginRoots(OadmPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = new List<string> { PluginPaths.Installed(paths.DataDirectory) };
        if (PluginPaths.Development() is { } development)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC listening on {Urls}")]
    private static partial void LogListening(ILogger logger, string urls);
}
