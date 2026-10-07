using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

public class OadmPathsTests
{
    [Fact]
    public void ExplicitDirectoryWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oadm-explicit");
        var paths = new OadmPaths(dir);

        Assert.Equal(Path.GetFullPath(dir), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "oadm.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.DataDirectory, "master.key"), paths.MasterKeyPath);
        Assert.Equal(Path.Combine(paths.DataDirectory, "plugins"), paths.PluginsDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "logs"), paths.LogsDirectory);
    }

    [Fact]
    public void EnvironmentVariableOverridesDefault()
    {
        // Same class as the default test, so xUnit never runs the two in parallel.
        var previous = Environment.GetEnvironmentVariable(OadmPaths.DataDirEnvironmentVariable);
        var dir = Path.Combine(Path.GetTempPath(), "oadm-from-env");
        try
        {
            Environment.SetEnvironmentVariable(OadmPaths.DataDirEnvironmentVariable, dir);
            Assert.Equal(Path.GetFullPath(dir), new OadmPaths().DataDirectory);
            Assert.Equal(Path.GetFullPath("explicit-wins"), new OadmPaths("explicit-wins").DataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OadmPaths.DataDirEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void DefaultsToLocalApplicationDataWhenNoOverride()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OadmPaths.DataDirEnvironmentVariable)))
        {
            return; // environment override set on this machine; covered by the explicit test
        }

        var paths = new OadmPaths();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "Oadm");
        Assert.Equal(Path.GetFullPath(expected), paths.DataDirectory);
    }
}
