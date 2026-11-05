using Microsoft.Data.Sqlite;

using Oadm.Contracts.Plugins;
using Oadm.Core.Plugins;

namespace Oadm.Server.Hosting;

/// <summary>
/// <c>Oadm.Server --self-check [--add-on &lt;folder&gt;]</c>: without a database file, a port or a service, checks what this published server
/// needs to run. The database library; every plugin next to the exe (and each unpacked add-on) loaded by the real loader, so its
/// classes are created; every assembly it references; and the plugin's own <see cref="Oadm.Sdk.Plugins.ICorePlugin.SelfCheck"/>. Prints each
/// problem and returns 1 (release.yml, smoke tests).
/// </summary>
public static class SelfCheck
{
    public const string Argument = "--self-check";

    public const string AddOnArgument = "--add-on";

    /// <summary>The folders named by <see cref="AddOnArgument"/>.</summary>
    public static IReadOnlyList<string> AddOnFolders(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return [.. args.Skip(1).Where((_, i) => string.Equals(args[i], AddOnArgument, StringComparison.OrdinalIgnoreCase))];
    }

    public static int Run(TextWriter output, string? appFolder = null, IReadOnlyList<string>? addOns = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var problems = new List<string>();
        try
        {
            using var database = new SqliteConnection("Data Source=:memory:");
            database.Open();
            output.WriteLine("SQLite: ok");
        }
#pragma warning disable CA1031 // Every failure of the check is a line of its report.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add("SQLite: " + ex.Message);
        }

        var root = Path.Combine(appFolder ?? AppContext.BaseDirectory, "plugins");
        var folders = (Directory.Exists(root) ? Directory.GetDirectories(root) : [])
            .Where(f => Directory.GetFiles(f, PluginLoader.ServerAssemblyPattern).Length > 0)
            .Order(StringComparer.Ordinal)
            .Concat(addOns ?? [])
            .ToList();
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
            if (loader.LoadPackage(folder) is { } package)
            {
                foreach (var assembly in package.Assemblies)
                {
                    problems.AddRange(PluginAssemblyCheck.Problems(assembly, package.LoadContext).Select(p => $"{name}: {p}"));
                }
            }
        }

        problems.AddRange(registry.Errors.Select(e => $"{Path.GetFileName(e.Source)}: {e.Message}"));
        foreach (var plugin in registry.AllCorePlugins)
        {
            try
            {
                if (plugin.Plugin.SelfCheck(plugin.Origin.Directory ?? string.Empty) is { } problem)
                {
                    problems.Add($"{plugin.Plugin.Id}: {problem}");
                }
            }
#pragma warning disable CA1031 // Plugin code is untrusted: every failure of the check is a line of its report.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                problems.Add($"{plugin.Plugin.Id}: {ex.Message}");
            }
        }

        if (folders.Count == 0)
        {
            problems.Add("No plugins next to the exe.");
        }

        output.WriteLine($"Plugins checked: {folders.Count}");
        foreach (var problem in problems)
        {
            output.WriteLine("PROBLEM " + problem);
        }

        return problems.Count == 0 ? 0 : 1;
    }
}
