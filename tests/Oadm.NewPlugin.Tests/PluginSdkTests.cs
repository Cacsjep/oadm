using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Oadm.NewPlugin.Tests;

/// <summary>
/// The plugin SDK packages (website dev/plugin-sdk.md): the project SDK builds plugins against the host's versions, and the
/// templates are the samples with the NuGet references, never a copy that drifts.
/// </summary>
public sealed partial class PluginSdkTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "oadm-templates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }

    [Fact]
    public void The_project_SDK_names_the_hosts_versions()
    {
        var versions = XDocument.Load(Path.Combine(Repository(), "src", "Oadm.Plugin.Sdk", "targets", "Oadm.Plugin.Versions.props"));
        string Property(string name) => versions.Descendants(name).Single().Value;
        var client = XDocument.Load(Path.Combine(Repository(), "src", "Oadm.Client", "Oadm.Client.csproj"));
        string ClientPackage(string id) => client.Descendants("PackageReference").Single(r => (string?)r.Attribute("Include") == id).Attribute("Version")!.Value;
        var sdk = File.ReadAllText(Path.Combine(Repository(), "src", "Oadm.Sdk", "Plugins", "Plugins.cs"));

        // A failure here: update src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Versions.props to what the host uses.
        Assert.Equal(SdkVersion().Match(sdk).Groups[1].Value, Property("OadmMinSdkVersion"));
        Assert.Equal(ClientPackage("Avalonia"), Property("OadmAvaloniaVersion"));
        Assert.Equal(ClientPackage("Avalonia.Controls.DataGrid"), Property("OadmDataGridVersion"));
        Assert.Equal(ClientPackage("CommunityToolkit.Mvvm"), Property("OadmCommunityToolkitVersion"));
        Assert.Equal(XDocument.Load(Path.Combine(Repository(), "Directory.Build.props")).Descendants("TargetFramework").Single().Value, Property("OadmTargetFramework"));
    }

    [Fact]
    public void Every_sample_becomes_a_template_on_the_packages_of_one_version()
    {
        var written = Templates.Write(Repository(), _output, "9.8.7");

        Assert.Equal(["oadm-action", "oadm-page", "oadm-task"], written.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var root in written)
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("Oadm.Samples.", text, StringComparison.Ordinal);
                Assert.DoesNotContain(@"..\..\src\", text, StringComparison.Ordinal);
            }

            foreach (var project in files.Where(f => f.EndsWith(".csproj", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(project);
                Assert.Contains(project.EndsWith(".Tests.csproj", StringComparison.Ordinal) ? "<Project Sdk=\"Microsoft.NET.Sdk\">" : "<Project Sdk=\"Oadm.Plugin.Sdk/9.8.7\">", text, StringComparison.Ordinal);
            }

            var tests = File.ReadAllText(files.Single(f => f.EndsWith(".Tests.csproj", StringComparison.Ordinal)));
            Assert.Contains("<PackageReference Include=\"Oadm.Sdk.Testing\" Version=\"9.8.7\" />", tests, StringComparison.Ordinal);

            using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".template.config", "template.json")));
            var sample = Scaffold.Samples.Single(s => "oadm-" + s.Kind == Path.GetFileName(root));
            Assert.Equal(sample.Name, template.RootElement.GetProperty("sourceName").GetString());
            Assert.Equal(sample.Id, template.RootElement.GetProperty("symbols").GetProperty("pluginId").GetProperty("replaces").GetString());
            Assert.All(template.RootElement.GetProperty("primaryOutputs").EnumerateArray(), o => Assert.True(File.Exists(Path.Combine(root, o.GetProperty("path").GetString()!))));

            using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")));
            Assert.Equal("9.8.7", tools.RootElement.GetProperty("tools").GetProperty("oadm.tools").GetProperty("version").GetString());
            Assert.Contains(".oadm/", File.ReadAllText(Path.Combine(root, ".gitignore")), StringComparison.Ordinal);

            // The rules for AI assistants: filled in by the template, with nothing that exists only in the OADM repository.
            var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
            Assert.Contains(sample.Id, agents, StringComparison.Ordinal);
            Assert.Contains(sample.DisplayName, agents, StringComparison.Ordinal);
            Assert.Contains($"https://oadm.app/dev/tutorial-{sample.Kind}/", agents, StringComparison.Ordinal);
            Assert.Contains("dotnet oadm dev start", agents, StringComparison.Ordinal);
            Assert.Contains("Device safety", agents, StringComparison.Ordinal);
            foreach (var repoOnly in new[] { "manage ", "CLAUDE.md", "docs/spec", "artifacts/", "src/Oadm", "dev-cameras" })
            {
                Assert.DoesNotContain(repoOnly, agents, StringComparison.Ordinal);
            }
        }
    }

    [GeneratedRegex("""public const string Version = "([^"]+)";""")]
    private static partial Regex SdkVersion();

    private static string Repository() => Oadm.Tests.Shared.TestRepository.Root;
}
