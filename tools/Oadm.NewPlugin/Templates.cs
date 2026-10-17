using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.NewPlugin;

/// <summary>
/// Writes the content of the NuGet package Oadm.Plugin.Templates from the samples (<c>dotnet new oadm-task</c>,
/// <c>oadm-page</c>, <c>oadm-action</c>): each sample's projects under the sample's short name (the template's
/// sourceName), the plugin parts on <c>Oadm.Plugin.Sdk/&lt;version&gt;</c>, the tests on the Oadm.Sdk packages of the same
/// version, plus a solution, the local tool manifest (the <c>oadm</c> tool of the same version), a .gitignore and a README.
/// The samples stay the single source: a template is never edited by hand.
/// </summary>
internal static partial class Templates
{
    /// <summary>Writes one template folder per sample into <paramref name="output"/> and returns them.</summary>
    public static IReadOnlyList<string> Write(string repository, string output, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var written = new List<string>();
        foreach (var sample in Scaffold.Samples)
        {
            var root = Path.Combine(output, "oadm-" + sample.Kind);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            var projects = new List<string>();
            foreach (var source in Directory.GetDirectories(Path.Combine(repository, "samples"), $"Oadm.Samples.{sample.Name}*")
                         .Where(d => Path.GetFileName(d) is var n && (n == $"Oadm.Samples.{sample.Name}" || n.StartsWith($"Oadm.Samples.{sample.Name}.", StringComparison.Ordinal)))
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var project = Path.GetFileName(source).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !IsBuildOutput(Path.GetRelativePath(source, f))))
                {
                    var relative = Path.GetRelativePath(source, file).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                    var path = Path.Combine(root, project, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var text = File.ReadAllText(file).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                    if (path.EndsWith(".csproj", StringComparison.Ordinal))
                    {
                        text = Project(text, version);
                        projects.Add($"{project}/{Path.GetFileName(path)}");
                    }

                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
            }

            WriteFile(root, $"{sample.Name}.slnx", Solution(projects));
            WriteFile(root, "Directory.Build.props", DirectoryProps);
            WriteFile(root, ".gitignore", "bin/\nobj/\n.oadm/\n.vs/\n*.user\nTestResults/\nProperties/launchSettings.json\n");
            WriteFile(root, Path.Combine(".config", "dotnet-tools.json"), ToolManifest(version));
            WriteFile(root, "README.md", Readme(sample));
            WriteFile(root, Path.Combine(".template.config", "template.json"), TemplateJson(sample, projects));
            written.Add(root);
        }

        return written;
    }

    /// <summary>A sample project as a template project: the SDK and packages of <paramref name="version"/> instead of src/.</summary>
    public static string Project(string text, string version)
    {
        var references = SdkReference().Replace(text, m => $"""<PackageReference Include="{m.Groups["name"].Value}" Version="{version}" />""");
        var isPluginPart = !references.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal);
        return isPluginPart
            ? references.Replace("""<Project Sdk="Microsoft.NET.Sdk">""", $"""<Project Sdk="Oadm.Plugin.Sdk/{version}">""", StringComparison.Ordinal)
            : references;
    }

    private const string DirectoryProps = """
        <Project>
          <!-- Every project of the plugin. The plugin parts use Oadm.Plugin.Sdk; after a build the plugin folder is
               .oadm/plugins/<plugin id>/, which "dotnet oadm dev start" loads. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <OadmDeployRoot>$(MSBuildThisFileDirectory).oadm/plugins/</OadmDeployRoot>
          </PropertyGroup>
        </Project>

        """;

    private static string Solution(IEnumerable<string> projects) =>
        "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n";

    private static string ToolManifest(string version) => new JsonObject
    {
        ["version"] = 1,
        ["isRoot"] = true,
        ["tools"] = new JsonObject
        {
            ["oadm.tools"] = new JsonObject { ["version"] = version, ["commands"] = new JsonArray("oadm"), ["rollForward"] = false },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static string Readme(Sample sample) => string.Create(CultureInfo.InvariantCulture, $"""
        # {sample.DisplayName}

        An OADM plugin: {sample.Description}.

        ```
        dotnet tool restore          # the oadm tool of this SDK version
        dotnet oadm dev start        # an OADM server for development, with this plugin
        dotnet build                 # the server restarts with the new build
        dotnet oadm dev client       # the OADM client against it
        dotnet test
        dotnet oadm pack             # the .oadmplugin file for customers
        ```

        Guide: https://oadm.app/dev/tutorial-{sample.Kind}/

        """);

    private static string TemplateJson(Sample sample, IReadOnlyList<string> projects)
    {
        var json = new JsonObject
        {
            ["$schema"] = "http://json.schemastore.org/template",
            ["author"] = "OADM contributors",
            ["classifications"] = new JsonArray("OADM", "Plugin", "Axis"),
            ["identity"] = "Oadm.Plugin.Templates." + sample.Kind,
            ["name"] = "OADM " + sample.Kind + " plugin",
            ["shortName"] = "oadm-" + sample.Kind,
            ["description"] = "An OADM plugin: " + sample.Description + ", with its tests.",
            ["tags"] = new JsonObject { ["language"] = "C#", ["type"] = "solution" },
            ["sourceName"] = sample.Name,
            ["preferNameDirectory"] = true,
            ["symbols"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "parameter",
                    ["datatype"] = "string",
                    ["description"] = "The plugin id, e.g. acme.door-overview. Default: community.<name in kebab case>.",
                    ["defaultValue"] = "",
                },
                ["displayName"] = new JsonObject
                {
                    ["type"] = "parameter",
                    ["datatype"] = "string",
                    ["description"] = "The name OADM shows. Default: the project name.",
                    ["defaultValue"] = "",
                },
                ["nameKebab"] = new JsonObject { ["type"] = "derived", ["valueSource"] = "name", ["valueTransform"] = "kebab" },
                ["defaultId"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "join",
                    ["parameters"] = new JsonObject
                    {
                        ["symbols"] = new JsonArray(
                            new JsonObject { ["type"] = "const", ["value"] = "community." },
                            new JsonObject { ["type"] = "ref", ["value"] = "nameKebab" }),
                    },
                },
                ["pluginId"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "coalesce",
                    ["parameters"] = new JsonObject { ["sourceVariableName"] = "id", ["fallbackVariableName"] = "defaultId" },
                    ["replaces"] = sample.Id,
                },
                ["pluginDisplayName"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "coalesce",
                    ["parameters"] = new JsonObject { ["sourceVariableName"] = "displayName", ["fallbackVariableName"] = "name" },
                    ["replaces"] = sample.DisplayName,
                },
            },
            ["forms"] = new JsonObject { ["kebab"] = new JsonObject { ["identifier"] = "kebabCase" } },
            ["primaryOutputs"] = new JsonArray([.. projects.Select(p => (JsonNode)new JsonObject { ["path"] = p })]),
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static void WriteFile(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static bool IsBuildOutput(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj" || first.StartsWith("TestResults", StringComparison.Ordinal);
    }

    /// <summary>A project reference to an SDK project of src/ (Oadm.Sdk, Oadm.Sdk.Client, Oadm.Sdk.Testing).</summary>
    [GeneratedRegex("""<ProjectReference Include="\.\.\\\.\.\\src\\(?<name>Oadm\.Sdk(\.Client|\.Testing)?)\\[^"]+\.csproj" />""")]
    private static partial Regex SdkReference();
}
