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
        }
        else
        {
            services.AddSingleton<IClientSettingsStore, JsonClientSettingsStore>();
            services.AddSingleton<IOadmApi>(sp =>
                new GrpcOadmApi(options.ServerAddress ?? sp.GetRequiredService<IClientSettingsStore>().Current.ServerAddress));
        }

        services.AddSingleton<DeviceStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<ServerConnection>();
        services.AddSingleton<TaskPluginCatalog>();
        services.AddSingleton<IClientPluginRegistry>(sp => new ClientPluginLoader(options, sp.GetRequiredService<ILogger<ClientPluginLoader>>()));
        services.AddSingleton<AvaloniaDialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<IUrlLauncher>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<TaskPluginRunner>();

        services.AddSingleton<ColumnLayoutViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();
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
