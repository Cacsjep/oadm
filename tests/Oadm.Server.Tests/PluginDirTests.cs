using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Oadm.Server.Hosting;

namespace Oadm.Server.Tests;

/// <summary>Oadm:PluginDir: plugins straight from a build output for plugin development.</summary>
public sealed class PluginDirTests
{
    [Fact]
    public void PluginDirsAreReadFromTheConfigurationInOrder()
    {
        var first = Path.Combine(Path.GetTempPath(), "acme", "bin");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [OadmServerHost.PluginDirKey] = $" {first} ; other ;{first}" })
            .Build();

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath("other")], OadmServerHost.PluginDirectories(configuration));
        Assert.Empty(OadmServerHost.PluginDirectories(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void PrintFoldersNamesTheDataAndPluginFoldersOfTheArguments()
    {
        var data = Path.Combine(Path.GetTempPath(), "oadm-folders-test");
        var build = Path.Combine(Path.GetTempPath(), "acme", "bin");

        using var report = JsonDocument.Parse(FolderReport.Build([FolderReport.Argument, $"--Oadm:DataDir={data}", $"--Oadm:PluginDir={build}"]));
        var root = report.RootElement;

        Assert.Equal("server", root.GetProperty("app").GetString());
        Assert.Equal(Path.GetFullPath(data), root.GetProperty("dataFolder").GetString());
        Assert.Equal(Path.Combine(Path.GetFullPath(data), "plugins"), root.GetProperty("deployFolder").GetString());
        Assert.Contains(Path.Combine(Path.GetFullPath(data), "plugins"), root.GetProperty("pluginFolders").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([Path.GetFullPath(build)], root.GetProperty("pluginDirs").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(FolderReport.ServiceName, root.GetProperty("service").GetProperty("name").GetString());
        Assert.False(Directory.Exists(data)); // nothing is created
    }
}
