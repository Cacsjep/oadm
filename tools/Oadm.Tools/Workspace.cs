using System.Text.Json;

namespace Oadm.Tools;

/// <summary>A plugin folder after a build: <c>.oadm/plugins/&lt;id&gt;/</c> with its plugin.json.</summary>
internal sealed record PluginFolder(string Id, string Version, string Path);

/// <summary>
/// The plugin project the tool works on: the folder with <c>.oadm/</c> (or a solution) from the current folder upward.
/// The plugin parts build into <c>.oadm/plugins/&lt;plugin id&gt;/</c> (Oadm.Plugin.Sdk, the templates set
/// OadmDeployRoot); the development server keeps its data in <c>.oadm/server-data</c>, the client in <c>.oadm/client-data</c>.
/// </summary>
internal sealed class Workspace
{
    private Workspace(string root) => Root = root;

    public string Root { get; }

    public string OadmFolder => Path.Combine(Root, ".oadm");

    public string PluginsFolder => Path.Combine(OadmFolder, "plugins");

    public string ServerData => Path.Combine(OadmFolder, "server-data");

    public string ClientData => Path.Combine(OadmFolder, "client-data");

    public string StateFile => Path.Combine(OadmFolder, "dev-server.json");

    /// <summary>The workspace of <paramref name="start"/>: the nearest folder upward with .oadm/, else with a solution, else the folder itself.</summary>
    public static Workspace Find(string start)
    {
        var full = Path.GetFullPath(start);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".oadm")))
            {
                return new Workspace(dir.FullName);
            }
        }

        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
            {
                return new Workspace(dir.FullName);
            }
        }

        return new Workspace(full);
    }

    /// <summary>The built plugin folders, by id.</summary>
    public IReadOnlyList<PluginFolder> Plugins() => Read(PluginsFolder);

    /// <summary>The built plugin folders, or what to do when there is none.</summary>
    public IReadOnlyList<PluginFolder> RequirePlugins()
    {
        var plugins = Plugins();
        return plugins.Count > 0
            ? plugins
            : throw new ToolException($"No plugin was built in {Root} yet ({PluginsFolder} is empty). Run: dotnet build");
    }

    /// <summary>Every <c>&lt;root&gt;/&lt;folder&gt;/plugin.json</c> below <paramref name="root"/>.</summary>
    public static IReadOnlyList<PluginFolder> Read(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = new List<PluginFolder>();
        foreach (var folder in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = Path.Combine(folder, "plugin.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                var id = json.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
                var version = json.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                list.Add(new PluginFolder(id ?? Path.GetFileName(folder), version ?? "1.0.0", folder));
            }
            catch (JsonException ex)
            {
                throw new ToolException($"{manifest} is not valid JSON: {ex.Message}");
            }
        }

        return list;
    }
}
