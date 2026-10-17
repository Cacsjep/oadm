using System.Reflection;

namespace Oadm.Tests.Shared;

/// <summary>
/// The repository the tests were built from (tests/Directory.Build.props writes its path into every test assembly),
/// independent of where the build output lives.
/// </summary>
internal static class TestRepository
{
    private static readonly Lazy<string> RootValue = new(() =>
        typeof(TestRepository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "RepositoryRoot")?.Value
        ?? throw new InvalidOperationException("The test assembly has no RepositoryRoot attribute (tests/Directory.Build.props)."));

    /// <summary>The repository root (the folder with Oadm.sln), without a trailing separator.</summary>
    public static string Root => System.IO.Path.TrimEndingDirectorySeparator(RootValue.Value);

    /// <summary>A path inside the repository.</summary>
    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary><c>artifacts/plugins</c>: the plugins a normal build deploys.</summary>
    public static string DevelopmentPlugins => Path("artifacts", "plugins");

    /// <summary><c>artifacts/test-plugins</c>: the test plugins a normal build deploys.</summary>
    public static string TestPlugins => Path("artifacts", "test-plugins");

    /// <summary>A deployed plugin folder; fails with what to build when it is missing.</summary>
    public static string DeployedPlugin(string root, string id, string dll)
    {
        var dir = System.IO.Path.Combine(root, id);
        Xunit.Assert.True(File.Exists(System.IO.Path.Combine(dir, dll)),
            $"{dll} is not deployed to {dir}. Build the solution once without OadmSkipPluginDeploy.");
        return dir;
    }
}
