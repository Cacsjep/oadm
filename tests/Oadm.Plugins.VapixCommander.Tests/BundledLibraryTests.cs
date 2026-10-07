namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>The battery-included library (plugins/Oadm.Plugins.VapixCommander/Library, copied next to the plugin) loads with this engine.</summary>
public sealed class BundledLibraryTests
{
    private static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "Library");

    [Fact]
    public void Every_bundled_command_loads_and_validates()
    {
        Assert.True(Directory.Exists(LibraryDirectory), "Library folder missing in the output: " + LibraryDirectory);

        var library = CommandLibrary.Load(LibraryDirectory);

        Assert.True(library.Problems.Count == 0, string.Join(Environment.NewLine, library.Problems));
        Assert.True(library.Commands.Count >= 100, $"only {library.Commands.Count} commands");
        Assert.All(library.Commands, c => Assert.Empty(CommandValidator.Validate(c)));
    }

    [Fact]
    public void Read_only_bundled_commands_without_required_inputs_render()
    {
        var library = CommandLibrary.Load(LibraryDirectory);
        var rendered = 0;
        foreach (var command in library.Commands.Where(c => !c.Writes && c.Fields.All(f => !f.IsRequired || f.Default is not null)))
        {
            var request = CommandRenderer.Render(command, Samples.Values());
            Assert.False(string.IsNullOrEmpty(request.RelativeUri));
            rendered++;
        }

        Assert.True(rendered > 10);
    }
}
