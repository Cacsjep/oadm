using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;

namespace Oadm.Client.Tests;

/// <summary>Where the client looks for plugins, including the macOS app bundle layout.</summary>
public sealed class PluginRootsTests
{
    [Fact]
    public void Inside_a_macos_app_bundle_the_resources_folder_is_a_plugin_root()
    {
        string app = Path.Combine(Path.GetTempPath(), "OADM.app");
        string macOs = Path.Combine(app, "Contents", "MacOS") + Path.DirectorySeparatorChar;

        Assert.Equal(Path.Combine(app, "Contents", "Resources"), ClientPluginLoader.AppBundleResources(macOs));
        Assert.Contains(Path.Combine(app, "Contents", "Resources", "plugins"), ClientPluginLoader.DefaultRoots(new AppOptions(), macOs));
    }

    [Fact]
    public void Outside_an_app_bundle_there_is_no_resources_root()
    {
        string plain = Path.Combine(Path.GetTempPath(), "oadm", "client");

        Assert.Null(ClientPluginLoader.AppBundleResources(plain));
        Assert.Null(ClientPluginLoader.AppBundleResources(Path.Combine(Path.GetTempPath(), "MacOS")));
        Assert.DoesNotContain(ClientPluginLoader.DefaultRoots(new AppOptions(), plain), r => r.Contains("Resources", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_dirs_of_the_command_line_come_first()
    {
        string build = Path.Combine(Path.GetTempPath(), "acme", "bin", "Debug");
        var options = AppOptions.Parse(["--plugin-dir", build, "--server", "localhost:5080", "--plugin-dir", "other"]);

        Assert.Equal([Path.GetFullPath(build), Path.GetFullPath("other")], options.PluginDirs);
        var roots = ClientPluginLoader.DefaultRoots(options, Path.Combine(Path.GetTempPath(), "oadm", "client"));
        Assert.Equal(Path.GetFullPath(build), roots[0]); // a build output wins over installed copies
        Assert.Equal(Path.GetFullPath("other"), roots[1]);
        Assert.Empty(AppOptions.Parse([]).PluginDirs);
    }
}
