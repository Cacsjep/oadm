using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Shell;

namespace Oadm.Client;

public partial class App : Application
{
    /// <summary>Set by Program (or tests) before the app starts.</summary>
    public static AppOptions Options { get; set; } = new();

    /// <summary>Log store already wired into Serilog by Program, if any.</summary>
    public static LogStore? LogStore { get; set; }

    public ServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ServiceRegistration.Build(Options, LogStore);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel vm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            vm.Start();
            desktop.ShutdownRequested += (_, _) => Services.GetRequiredService<ServerConnection>().Stop();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
