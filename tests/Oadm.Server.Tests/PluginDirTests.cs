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
}
