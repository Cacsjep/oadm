using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.LiveView;
using Oadm.Client.Logging;
using Oadm.Client.Plugins;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;

using Serilog;

namespace Oadm.Client.Infrastructure;

/// <summary>Client composition root.</summary>
public static class ServiceRegistration
{
    public static ServiceProvider Build(AppOptions options, LogStore? logStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton(options);
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton(sp => logStore ?? new LogStore(sp.GetRequiredService<IUiDispatcher>()));

        if (options.UseFake)
        {
            services.AddSingleton<IClientSettingsStore, InMemoryClientSettingsStore>();
            services.AddSingleton<IOadmApi>(_ => new FakeOadmApi());
            services.AddSingleton<IServerTrust, NoServerTrust>();
        }
        else
        {
            services.AddSingleton<IClientSettingsStore, JsonClientSettingsStore>();
            // TLS with the pinned server certificate (client settings), the login token on every call.
            services.AddSingleton(sp => new Oadm.Contracts.Security.ServerCertificatePinning(new ClientPinStore(sp.GetRequiredService<IClientSettingsStore>())));
            services.AddSingleton(sp => new GrpcOadmApi(
                options.ServerAddress ?? sp.GetRequiredService<IClientSettingsStore>().Current.ServerAddress,
                sp.GetRequiredService<Oadm.Contracts.Security.ServerCertificatePinning>()));
            services.AddSingleton<IOadmApi>(sp => sp.GetRequiredService<GrpcOadmApi>());
            services.AddSingleton<IServerTrust>(sp => new GrpcServerTrust(sp.GetRequiredService<GrpcOadmApi>()));
        }

        services.AddSingleton(_ =>
        {
            var session = new UserSession();
            if (options.UseFake)
            {
                // Fake mode has no login: the administrator "admin".
                session.SignIn(new Contracts.V1.UserInfo { UserName = FakeOadmApi.FakeUserName, Role = Contracts.V1.UserRole.Admin }, "fake");
            }

            return session;
        });
        services.AddTransient<LoginViewModel>();
        services.AddSingleton<Func<LoginViewModel>>(sp => () => sp.GetRequiredService<LoginViewModel>());
        services.AddSingleton<AppShell>();

        services.AddSingleton<Tags.TagStore>();
        services.AddSingleton<DeviceStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<ServerConnection>();
        services.AddSingleton<TaskPluginCatalog>();
        services.AddSingleton<PluginPackageStore>();
        services.AddSingleton<IClientPluginRegistry>(sp => new ClientPluginLoader(options, sp.GetRequiredService<ILogger<ClientPluginLoader>>()));
        services.AddSingleton<AvaloniaDialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<IUrlLauncher>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<TaskPluginRunner>();

        services.AddSingleton<ColumnLayoutViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CredentialsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<AuditLogViewModel>();
        services.AddSingleton(sp => new UsersViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<UserSession>(),
            sp.GetRequiredService<ILogger<UsersViewModel>>(),
            sp.GetRequiredService<ServerConnection>()));
        services.AddSingleton<Func<AddDevicesMode, AddDevicesViewModel>>(sp => mode => new AddDevicesViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILogger<AddDevicesViewModel>>(),
            mode));

        // Toolbar plugins: the built-in ones are registered like plugin parts; DeviceToolbar adds those of client plugins.
        foreach (Oadm.Sdk.Client.IToolbarPlugin plugin in BuiltInToolbarPlugins.All)
        {
            services.AddSingleton(plugin);
        }

        services.AddSingleton<DeviceToolbar>();
        services.AddSingleton<IVideoDecoderFactory, FfmpegVideoDecoderFactory>();
        services.AddSingleton<LiveViewViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
