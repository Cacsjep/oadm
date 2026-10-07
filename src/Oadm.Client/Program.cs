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
        AppOptions options = AppOptions.Parse(args);
        var logStore = new LogStore(new AvaloniaUiDispatcher());
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(options.DataFolder, "logs", "client-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.Sink(new LogStoreSink(logStore))
            .CreateLogger();

        try
        {
            Log.Information("OADM client starting ({Mode})", options.UseFake ? "fake server" : "gRPC");
            App.Options = options;
            App.LogStore = logStore;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
