using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

using Oadm.Core.Persistence;
using Oadm.Core.Plugins;

namespace Oadm.Server.Hosting;

/// <summary>
/// <c>Oadm.Server --print-folders</c>: prints where this server keeps its data and plugins as JSON and exits, without
/// opening the database or a port. The <c>oadm</c> tool runs the installed server with the service's own arguments and
/// environment, so the answer is the service's folders, never a guess (docs/plugin-sdk.md).
/// </summary>
public static class FolderReport
{
    public const string Argument = "--print-folders";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a console answer, never HTML
    };

    /// <summary>The folders for these arguments: the same configuration sources as the host (appsettings.json, environment, command line).</summary>
    public static string Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine([.. args.Where(a => !string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase))])
            .Build();
        var paths = new OadmPaths(configuration["Oadm:DataDir"]);
        return JsonSerializer.Serialize(new
        {
            App = "server",
            Version = typeof(FolderReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?",
            AppFolder = AppContext.BaseDirectory,
            DataFolder = paths.DataDirectory,
            LogsFolder = paths.LogsDirectory,
            // Where "oadm deploy --installed" copies a plugin: the data folder's plugins (admin-only as a service).
            DeployFolder = PluginPaths.Installed(paths.DataDirectory),
            PluginFolders = OadmServerHost.DefaultPluginRoots(paths, includeDevelopment: false),
            DevelopmentPluginFolder = PluginPaths.Development(),
            PluginDirs = OadmServerHost.PluginDirectories(configuration),
            Service = new { Name = ServiceName, Manager = ServiceManager },
        }, Json);
    }

    /// <summary>The installed service of this OS: Windows service, systemd unit or LaunchDaemon label.</summary>
    public static string ServiceName =>
        OperatingSystem.IsWindows() ? ServiceHosting.WindowsServiceName
        : OperatingSystem.IsMacOS() ? "com.oadm.server"
        : "oadm-server";

    private static string ServiceManager =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "launchd" : "systemd";
}
