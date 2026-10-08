using Avalonia;

using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;

using Serilog;

namespace Oadm.Client;

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(LiveView.DecoderSelfCheck.Argument, StringComparer.OrdinalIgnoreCase))
        {
            return LiveView.DecoderSelfCheck.Run(Console.Out);
        }

        AppOptions options = AppOptions.Parse(args);
        var logStore = new LogStore(new AvaloniaUiDispatcher());
        string logFolder = Path.Combine(options.DataFolder, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Capped (never fills the disk): a new file per day or at 10 MB, files of the last 14 days, at most 30 files
            // (at most about 300 MB).
            .WriteTo.File(Path.Combine(logFolder, "client-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10L * 1024 * 1024, rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30, retainedFileTimeLimit: TimeSpan.FromDays(14),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.Sink(new LogStoreSink(logStore))
            .CreateLogger();

        // Global handlers: every error is logged with the log path; UI errors are shown and the client keeps running.
        using var loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger);
        var crashes = new CrashHandling(loggerFactory.CreateLogger("Oadm.Client.Crash"), CrashHandling.TodaysLogFile(logFolder), ShowErrorAsync);
        crashes.InstallProcessHandlers();

        try
        {
            Log.Information("OADM client starting ({Mode})", options.UseFake ? "fake server" : "gRPC");
            App.Options = options;
            App.LogStore = logStore;
            return BuildAvaloniaApp().AfterSetup(_ => crashes.InstallUiHandler()).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "OADM client crashed");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Shows an error in the shared message window over the main window (nothing without one).</summary>
    private static async Task ShowErrorAsync(string title, string message)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
        {
            await Oadm.Sdk.Client.Controls.MessageWindow.ShowMessageAsync(owner, title, message).ConfigureAwait(true);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
