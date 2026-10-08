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
        ApplyCrispTextToAllWindows();
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

    /// <summary>
    /// Crisp text in every window (main window, dialogs, plugin windows): subpixel anti-aliasing
    /// (ClearType-like), strong hinting and baselines on whole device pixels. Avalonia 12 exposes these
    /// only as methods, so they are applied when each window is created.
    /// </summary>
    public static void ApplyCrispTextToAllWindows() =>
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) =>
        {
            ApplyCrispText(window);
            window.Icon ??= AppIcon.Value;
        });

    /// <summary>
    /// The OADM logo (packaging/icons/oadm-256.png, built from /icon) as window icon of every window: taskbar,
    /// Alt+Tab and the Linux window list. The exe icon alone is not enough (dotnet exec, Linux, macOS).
    /// </summary>
    private static readonly Lazy<Avalonia.Controls.WindowIcon?> AppIcon = new(() =>
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Oadm.Client/Assets/oadm-256.png"));
            return new Avalonia.Controls.WindowIcon(stream);
        }
#pragma warning disable CA1031 // A missing icon must never stop a window from opening.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    });

    public static void ApplyCrispText(Avalonia.Visual visual)
    {
        Avalonia.Media.TextOptions.SetTextRenderingMode(visual, Avalonia.Media.TextRenderingMode.SubpixelAntialias);
        Avalonia.Media.TextOptions.SetTextHintingMode(visual, Avalonia.Media.TextHintingMode.Strong);
        Avalonia.Media.TextOptions.SetBaselinePixelAlignment(visual, Avalonia.Media.BaselinePixelAlignment.Aligned);
    }
}
