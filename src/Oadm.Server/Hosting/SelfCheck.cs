using Microsoft.Data.Sqlite;

using Oadm.Contracts.Plugins;
using Oadm.Core.Plugins;

namespace Oadm.Server.Hosting;

/// <summary>
/// <c>Oadm.Server --self-check</c>: without a database file, a port or a service, checks what this published server needs to run: the
/// database library and every assembly of every plugin next to the exe. Prints each problem and returns 1 (release.yml, smoke tests).
/// </summary>
public static class SelfCheck
{
    public const string Argument = "--self-check";

    public static int Run(TextWriter output, string? appFolder = null)
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

        var checkedParts = 0;
        var root = Path.Combine(appFolder ?? AppContext.BaseDirectory, "plugins");
        foreach (var folder in (Directory.Exists(root) ? Directory.GetDirectories(root) : []).Order(StringComparer.Ordinal))
        {
            foreach (var dll in Directory.GetFiles(folder, PluginLoader.ServerAssemblyPattern).Order(StringComparer.Ordinal))
            {
                checkedParts++;
                var name = Path.GetFileName(folder);
                var context = new PluginLoadContext(dll, "self-check:" + name);
                try
                {
                    problems.AddRange(PluginAssemblyCheck.Problems(context.LoadFromFileWithoutLock(dll), context).Select(p => $"{name}: {p}"));
                }
#pragma warning disable CA1031 // Every failure of the check is a line of its report.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    problems.Add($"{name}: {ex.Message}");
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        if (checkedParts == 0)
        {
            problems.Add("No plugins next to the exe.");
        }

        output.WriteLine($"Plugins checked: {checkedParts}");
        foreach (var problem in problems)
        {
            output.WriteLine("PROBLEM " + problem);
        }

        return problems.Count == 0 ? 0 : 1;
    }
}
