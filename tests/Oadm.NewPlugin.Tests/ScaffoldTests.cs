using System.Reflection;

namespace Oadm.NewPlugin.Tests;

/// <summary>"manage new": the samples of this repository copied under a new name into a temporary repository.</summary>
public sealed class ScaffoldTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "oadm-new-plugin-" + Guid.NewGuid().ToString("N"));

    public ScaffoldTests()
    {
        var samples = Path.Combine(Repository(), "samples");
        foreach (var file in Directory.EnumerateFiles(samples, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(samples, file);
            if (relative.Split(Path.DirectorySeparatorChar)[1..].FirstOrDefault() is "bin" or "obj")
            {
                continue;
            }

            var target = Path.Combine(_repo, "samples", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Theory]
    [InlineData("task", 3)]
    [InlineData("page", 3)]
    [InlineData("action", 2)]
    public void Every_project_of_the_sample_is_copied_under_the_new_name(string kind, int projects)
    {
        var sample = Scaffold.Samples.Single(s => s.Kind == kind);
        var written = Scaffold.Create(_repo, sample, Scaffold.Names("My cool plugin", null));

        Assert.Equal(projects, written.Count);
        Assert.Contains(Path.Combine(_repo, "plugins", "Oadm.Plugins.MyCoolPlugin", "Oadm.Plugins.MyCoolPlugin.csproj"), written);
        Assert.Contains(Path.Combine(_repo, "tests", "Oadm.Plugins.MyCoolPlugin.Tests", "Oadm.Plugins.MyCoolPlugin.Tests.csproj"), written);

        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories).Where(f => !f.Contains("samples", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Samples", text, StringComparison.Ordinal);
            Assert.DoesNotContain(sample.Name, text, StringComparison.Ordinal);
            Assert.DoesNotContain(sample.Id, text, StringComparison.Ordinal);
            Assert.DoesNotContain(sample.Name, Path.GetFileName(file), StringComparison.Ordinal);
        }

        var plugin = File.ReadAllText(Path.Combine(_repo, "plugins", "Oadm.Plugins.MyCoolPlugin", "Oadm.Plugins.MyCoolPlugin.csproj"));
        Assert.Contains("<OadmPluginId>community.my-cool-plugin</OadmPluginId>", plugin, StringComparison.Ordinal);
        Assert.Contains("Oadm.Plugin.Repo.props", File.ReadAllText(Path.Combine(_repo, "plugins", "Oadm.Plugins.MyCoolPlugin", "Directory.Build.props")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_test_project_points_to_the_new_plugin_and_the_test_helpers()
    {
        Scaffold.Create(_repo, Scaffold.Samples.Single(s => s.Kind == "task"), Scaffold.Names("My cool plugin", "acme.cool"));

        var tests = File.ReadAllText(Path.Combine(_repo, "tests", "Oadm.Plugins.MyCoolPlugin.Tests", "Oadm.Plugins.MyCoolPlugin.Tests.csproj"));
        Assert.Contains(@"..\..\plugins\Oadm.Plugins.MyCoolPlugin\Oadm.Plugins.MyCoolPlugin.csproj", tests, StringComparison.Ordinal);
        Assert.Contains(@"..\..\src\Oadm.Sdk.Testing\Oadm.Sdk.Testing.csproj", tests, StringComparison.Ordinal);
        Assert.Contains("acme.cool", File.ReadAllText(Path.Combine(_repo, "plugins", "Oadm.Plugins.MyCoolPlugin", "Oadm.Plugins.MyCoolPlugin.csproj")), StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_plugin_is_never_overwritten()
    {
        var sample = Scaffold.Samples.Single(s => s.Kind == "action");
        Scaffold.Create(_repo, sample, Scaffold.Names("Door bell", null));

        var error = Assert.Throws<InvalidOperationException>(() => Scaffold.Create(_repo, sample, Scaffold.Names("Door bell", null)));
        Assert.Contains("exists already", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("my cool plugin", "MyCoolPlugin", "my-cool-plugin")]
    [InlineData("  Door   bell 2 ", "DoorBell2", "door-bell-2")]
    public void Names_are_derived_from_what_the_developer_typed(string typed, string pascal, string kebab)
    {
        var names = Scaffold.Names(typed, null);
        Assert.Equal((pascal, kebab, "community." + kebab), (names.Pascal, names.Kebab, names.Id));
    }

    [Theory]
    [InlineData("2 cool", null)]
    [InlineData("cool-plugin", null)]
    [InlineData("Task", null)]
    [InlineData("Cool", "Acme.Cool")]
    [InlineData("Cool", "cool")]
    public void Unusable_names_and_ids_are_refused(string name, string? id)
    {
        Assert.Throws<ArgumentException>(() => Scaffold.Names(name, id));
    }

    private static string Repository() =>
        typeof(ScaffoldTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepositoryRoot").Value!;
}
