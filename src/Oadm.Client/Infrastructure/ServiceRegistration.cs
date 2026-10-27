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
            services.AddSingleton<IClientSettingsStore>(_ =>
            {
                var store = new InMemoryClientSettingsStore();
                store.Current.Appearance = Oadm.Theme.OadmColors.Current; // the appearance the client started with
                return store;
            });
            services.AddSingleton<IOadmApi>(_ =>
            {
                var fake = new FakeOadmApi(simulationTime: options.FakeSimulationTime);
                if (options.FakeUser is { } user)
                {
                    fake.CurrentUserName = user; // a sample user, e.g. tech2 with the device group Building B
                }

                return fake;
            });
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

        services.AddSingleton(sp =>
        {
            var session = new UserSession();
            if (options.UseFake)
            {
                // Fake mode has no login: the administrator "admin", or the sample user of --fake-user with its role and devices.
                session.SignIn(sp.GetRequiredService<IOadmApi>() is FakeOadmApi fake
                    ? fake.CurrentUser()
                    : new Contracts.V1.UserInfo
                    {
                        UserName = FakeOadmApi.FakeUserName,
                        Role = Contracts.V1.UserRole.Admin,
                        RoleId = FakeOadmApi.AdministratorRoleId,
                        RoleName = "Administrator",
                        AllPermissions = true,
                    }, "fake");
            }

            return session;
        });
        services.AddTransient<LoginViewModel>();
        services.AddSingleton<Func<LoginViewModel>>(sp => () => sp.GetRequiredService<LoginViewModel>());
        services.AddSingleton<AppShell>();
        services.AddSingleton<Updates.IReleaseCheck>(_ => Updates.GitHubReleaseCheck.Create());
        services.AddSingleton<Updates.UpdateNotifier>();

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
        services.AddSingleton<EventRules.EventRulesViewModel>();
        services.AddSingleton<Sites.SitesViewModel>();
        services.AddSingleton<Compliance.ComplianceViewModel>();
        services.AddSingleton<Backup.BackupViewModel>();
        services.AddSingleton(sp =>
        {
            var license = new Licensing.LicenseViewModel(
                sp.GetRequiredService<IOadmApi>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IClipboardService>(),
                sp.GetRequiredService<IUrlLauncher>(),
                sp.GetRequiredService<UserSession>());
            // PRO plugins turn on or off with the license: the plugin list, rail pages and menus follow.
            license.LicenseChanged += (_, _) =>
            {
                _ = sp.GetRequiredService<PluginPackageStore>().LoadAsync(CancellationToken.None);
                sp.GetRequiredService<Plugins.TaskPluginCatalog>().RequestRefresh(); // tasks shown only with PRO (EST's Certificate enrollment)
            };
            return license;
        });
        services.AddSingleton(sp => new Support.SupportViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IDialogService>(),
            Path.Combine(options.DataFolder, "logs")));
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CredentialsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<AuditLogViewModel>();
        services.AddSingleton<ServerLogViewModel>();
        services.AddSingleton(sp => new RolesViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<UserSession>(),
            sp.GetRequiredService<ServerConnection>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<DeviceStore>();
            return new DeviceGroupsViewModel(
                sp.GetRequiredService<IOadmApi>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<UserSession>(),
                () => [.. store.Devices]);
        });
        services.AddSingleton(sp => new UsersViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<UserSession>(),
            sp.GetRequiredService<ILogger<UsersViewModel>>(),
            sp.GetRequiredService<ServerConnection>(),
            sp.GetRequiredService<RolesViewModel>(),
            sp.GetRequiredService<DeviceGroupsViewModel>()));
        services.AddSingleton<Func<AddDevicesMode, AddDevicesViewModel>>(sp => mode => new AddDevicesViewModel(
            sp.GetRequiredService<IOadmApi>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<ILogger<AddDevicesViewModel>>(),
            mode,
            sp.GetService<IClientSettingsStore>())
        {
            // The credential list is Administrator only (the server checks it too).
            CanSaveToCredentialList = sp.GetService<UserSession>()?.IsAdmin ?? true,
            HasSites = sp.GetService<Oadm.Client.Devices.DeviceStore>()?.Sites.HasSites ?? false,
            Dialogs = sp.GetService<Oadm.Client.Dialogs.IDialogService>(),
        });

        // Toolbar plugins: the built-in ones are registered like plugin parts; DeviceToolbar adds those of client plugins.
        foreach (Oadm.Sdk.Client.IToolbarPlugin plugin in BuiltInToolbarPlugins.All)
        {
            services.AddSingleton(plugin);
        }

        services.AddSingleton<DeviceToolbar>();
        services.AddSingleton<GpuDecodingService>();
        services.AddSingleton<IVideoDecoderFactory, FfmpegVideoDecoderFactory>();
        services.AddSingleton(sp => new LiveVideoSettings(sp.GetService<IClientSettingsStore>()));
        services.AddSingleton<LiveViewViewModel>();
        services.AddSingleton<Oadm.Sdk.Client.ILiveVideoAccess, LiveVideoAccess>();
        services.AddSingleton<DevicesViewModel>();
        // Help: a new window view model each time the Help window opens (its docs server runs while it is open).
        services.AddSingleton<Func<HelpWindowViewModel>>(sp => () => new HelpWindowViewModel(
            Help.HelpDocs.FindRoot(),
            Help.EmbeddedBrowser.Check,
            sp.GetRequiredService<IUrlLauncher>(),
            sp.GetRequiredService<ILogger<HelpWindowViewModel>>()));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
