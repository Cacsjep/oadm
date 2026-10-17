using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oadm.NewPlugin;

/// <summary>A tutorial sample in samples/: its name in code, its plugin id and display name, and what it is.</summary>
internal sealed record Sample(string Kind, string Name, string Id, string DisplayName, string Description);

/// <summary>The new plugin's names, from what the developer typed ("My cool plugin").</summary>
internal sealed record PluginNames(string DisplayName, string Pascal, string Kebab, string Id);

/// <summary>
/// Creates a plugin from a sample: every project of the sample (server part, client part, tests) is copied with its name,
/// namespaces, plugin id and display name replaced, so the new plugin builds, passes its tests and runs like the sample.
/// </summary>
internal static partial class Scaffold
{
    public static readonly IReadOnlyList<Sample> Samples =
    [
        new("task", "UpnpName", "acme.upnp-name", "Set UPnP name", "a task plugin with a dialog (context menu, one task per device)"),
        new("page", "FirmwareOverview", "acme.firmware-overview", "Firmware overview", "a core plugin with its own page in the navigation rail"),
        new("action", "VirtualInput", "acme.virtual-input", "Set virtual input", "an event rule action"),
    ];

    /// <summary>Names a type of the plugin would hide or that make no sense as a plugin name.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Task", "Tasks", "Plugin", "Plugins", "Client", "Server", "Tests", "Sdk", "Core", "Oadm", "System", "Version", "Guid", "String",
    };

    /// <summary>The names, or the problem with the input.</summary>
    public static PluginNames Names(string name, string? id)
    {
        var display = Regex.Replace(name.Trim(), @"\s+", " ");
        if (!NameRegex().IsMatch(display))
        {
            throw new ArgumentException("The name needs letters, digits and spaces only, starting with a letter (\"My cool plugin\").");
        }

        var words = display.Split(' ');
        var pascal = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        if (Reserved.Contains(pascal))
        {
            throw new ArgumentException($"\"{display}\" would clash with a name in the code. Choose a more specific name.");
        }

        var kebab = string.Join("-", words.Select(w => w.ToLowerInvariant()));
        id ??= "community." + kebab;
        if (!IdRegex().IsMatch(id))
        {
            throw new ArgumentException("The id needs lower-case parts separated by dots, like \"acme.my-cool-plugin\".");
        }

        return new PluginNames(display, pascal, kebab, id);
    }

    /// <summary>
    /// Writes the plugin into <paramref name="repository"/>/plugins and /tests and returns the project files it wrote. Throws
    /// <see cref="InvalidOperationException"/> when a folder exists already; nothing is written then.
    /// </summary>
    public static IReadOnlyList<string> Create(string repository, Sample sample, PluginNames names)
    {
        var projects = Directory.GetDirectories(Path.Combine(repository, "samples"), $"Oadm.Samples.{sample.Name}*")
            .Where(d => Path.GetFileName(d) is var n && (n == $"Oadm.Samples.{sample.Name}" || n.StartsWith($"Oadm.Samples.{sample.Name}.", StringComparison.Ordinal)))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        if (projects.Count == 0)
        {
            throw new InvalidOperationException($"The sample {sample.Name} is missing in {Path.Combine(repository, "samples")}.");
        }

        var targets = projects.ToDictionary(p => p, p => Target(repository, Path.GetFileName(p), sample, names));
        if (targets.Values.FirstOrDefault(Directory.Exists) is { } existing)
        {
            throw new InvalidOperationException($"{Path.GetRelativePath(repository, existing)} exists already. Choose another name.");
        }

        var written = new List<string>();
        foreach (var (source, target) in targets)
        {
            var isTests = source.EndsWith(".Tests", StringComparison.Ordinal);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                         .Where(f => !IsBuildOutput(Path.GetRelativePath(source, f))))
            {
                var relative = Path.GetRelativePath(source, file).Replace(sample.Name, names.Pascal, StringComparison.Ordinal);
                var path = Path.Combine(target, relative.Replace("Oadm.Samples.", "Oadm.Plugins.", StringComparison.Ordinal));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Rewrite(File.ReadAllText(file), sample, names, isTests), new UTF8Encoding(false));
                if (path.EndsWith(".csproj", StringComparison.Ordinal))
                {
                    written.Add(path);
                }
            }

            if (!isTests)
            {
                // The samples get the plugin project SDK from samples/Directory.Build.*; a plugin in plugins/ from its own.
                File.WriteAllText(Path.Combine(target, "Directory.Build.props"), RepoProps, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(target, "Directory.Build.targets"), RepoTargets, new UTF8Encoding(false));
            }
        }

        return written;
    }

    /// <summary>The plugin project SDK from source (src/Oadm.Plugin.Sdk), deployed to artifacts/plugins like every plugin.</summary>
    public const string RepoProps = """
        <Project>
          <!-- The plugin project SDK from source (src/Oadm.Plugin.Sdk/targets): references to src/, plugin.json from the
               project's properties, the plugin folder in artifacts/plugins/. -->
          <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
          <Import Project="../../src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Repo.props" />
        </Project>

        """;

    /// <summary>The second half of <see cref="RepoProps"/>.</summary>
    public const string RepoTargets = """
        <Project>
          <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', '$(MSBuildThisFileDirectory)../'))" />
          <Import Project="../../src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Repo.targets" />
        </Project>

        """;

    /// <summary>The sample's names replaced by the new ones; test projects point to plugins/.</summary>
    public static string Rewrite(string text, Sample sample, PluginNames names, bool isTests)
    {
        if (isTests)
        {
            text = text.Replace($@"..\Oadm.Samples.{sample.Name}", $@"..\..\plugins\Oadm.Plugins.{sample.Name}", StringComparison.Ordinal);
        }

        return text
            .Replace("Oadm.Samples." + sample.Name, "Oadm.Plugins." + names.Pascal, StringComparison.Ordinal)
            .Replace(sample.Id, names.Id, StringComparison.Ordinal)
            .Replace(sample.DisplayName, names.DisplayName, StringComparison.Ordinal)
            .Replace(sample.Name, names.Pascal, StringComparison.Ordinal);
    }

    private static string Target(string repository, string sampleProject, Sample sample, PluginNames names)
    {
        var project = sampleProject.Replace("Oadm.Samples." + sample.Name, "Oadm.Plugins." + names.Pascal, StringComparison.Ordinal);
        return Path.Combine(repository, project.EndsWith(".Tests", StringComparison.Ordinal) ? "tests" : "plugins", project);
    }

    private static bool IsBuildOutput(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj" || first.StartsWith("TestResults", StringComparison.Ordinal);
    }

    /// <summary>"Created ... from the task sample" and what to do next.</summary>
    public static string Report(string repository, Sample sample, PluginNames names, IReadOnlyList<string> projects) =>
        string.Create(CultureInfo.InvariantCulture, $"""
            Created {sample.Description} "{names.DisplayName}" (id {names.Id}) from the {sample.Kind} sample:
            {string.Join(Environment.NewLine, projects.Select(p => "  " + Path.GetRelativePath(repository, p).Replace('\\', '/')))}

            It builds, its tests pass and it runs like the sample. Next:
              dotnet test tests/Oadm.Plugins.{names.Pascal}.Tests
              ./manage.sh run dev          (or .\manage.ps1 run dev)
            Then change the code: the tutorial explains every file, https://cacsjep.github.io/oadm/dev/tutorial-{sample.Kind}/
            """);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*( [A-Za-z0-9]+)*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$")]
    private static partial Regex IdRegex();
}
