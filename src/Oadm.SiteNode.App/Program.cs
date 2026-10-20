using Avalonia;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;

namespace Oadm.SiteNode.App;

/// <summary>
/// OADM Site Node app. --data-dir &lt;folder&gt; talks to a node with its own data folder (like the service's option).
/// --admin-request &lt;request&gt; --out &lt;file or -&gt;: the elevated copy that sends one change (see <see cref="SystemElevation"/>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var dataDirectory = SystemElevation.Value(args, "--data-dir");
        var endpoint = dataDirectory is null ? AdminEndpoint.Default : AdminEndpoint.ForDataDirectory(dataDirectory);
        if (args.Contains(SystemElevation.RequestArgument))
        {
            // A one-shot process without a window or UI thread: blocking on the one request is its whole life.
            return SystemElevation.AnswerAsync(args, endpoint).GetAwaiter().GetResult();
        }

        App.Start = new AppStart(endpoint, dataDirectory, AppSettingsStore.DefaultFolder);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
