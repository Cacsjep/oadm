using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Plugins;

/// <summary>
/// <c>Oadm.Client --print-folders</c>: prints where this client keeps its settings and plugins as JSON and exits, without
/// a window. The <c>oadm</c> tool asks the installed client this way instead of guessing (docs/plugin-sdk.md).
/// </summary>
internal static class ClientFolderReport
{
    public const string Argument = "--print-folders";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a console answer, never HTML
    };

    public static string Build(AppOptions options, string? appDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string baseDirectory = appDirectory ?? AppContext.BaseDirectory;
        return JsonSerializer.Serialize(new
        {
            App = "client",
            Version = typeof(ClientFolderReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?",
            AppFolder = baseDirectory,
            DataFolder = options.DataFolder,
            SettingsFile = Path.Combine(options.DataFolder, "client-settings.json"),
            LogsFolder = Path.Combine(options.DataFolder, "logs"),
            // Where "oadm deploy --installed" copies a client part: this user's plugins folder (no admin rights needed).
            DeployFolder = Path.Combine(options.DataFolder, "plugins"),
            PluginFolders = ClientPluginLoader.DefaultRoots(options with { PluginDirs = [] }, baseDirectory)
                .Where(r => !string.Equals(r, ClientPluginLoader.DevelopmentRoot(baseDirectory), StringComparison.OrdinalIgnoreCase)),
            DevelopmentPluginFolder = ClientPluginLoader.DevelopmentRoot(baseDirectory),
            PluginDirs = options.PluginDirs,
        }, Json);
    }
}
