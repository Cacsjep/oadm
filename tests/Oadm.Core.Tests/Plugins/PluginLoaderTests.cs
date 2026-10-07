using System.Runtime.Loader;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Plugins.Restart;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly PluginRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "oadm-plugin-tests-" + Guid.NewGuid().ToString("N"));

    public PluginLoaderTests()
    {
        _loader = new PluginLoader(_registry);
    }

    /// <summary>artifacts/test-plugins/oadm.sample, deployed by building Oadm.TestPlugins.Sample.</summary>
    private static string SampleDirectory
    {
        get
        {
            var repo = PluginPaths.FindRepositoryRoot(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException("Repository root not found.");
            var dir = Path.Combine(repo, "artifacts", "test-plugins", "oadm.sample");
            Assert.True(File.Exists(Path.Combine(dir, "Oadm.TestPlugins.Sample.Server.dll")), $"Sample plugin not built into {dir}");
            return dir;
        }
    }

    public void Dispose()
    {
        foreach (var package in _loader.Packages)
        {
            package.LoadContext.Unload();
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void LoadsCorePluginAndItsContributedTaskFromTheBuiltSample()
    {
        var package = _loader.LoadPackage(SampleDirectory);

        Assert.NotNull(package);
        Assert.Empty(_registry.Errors);
        Assert.Equal("oadm.sample", package.Manifest.Id);
        Assert.Equal("1.0.0", package.Manifest.Version);
        Assert.True(package.LoadContext.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, package.LoadContext);
        Assert.Same(package.LoadContext, AssemblyLoadContext.GetLoadContext(package.Assemblies[0]));

        // The SDK is shared with the host, so interface identity holds.
        Assert.False(File.Exists(Path.Combine(SampleDirectory, "Oadm.Sdk.dll")));
        var core = Assert.Single(_registry.CorePlugins);
        Assert.Equal("oadm.sample", core.Id);
        Assert.Equal("oadm.sample", core.Origin.PackageId);

        Assert.True(_registry.TryGetTaskPlugin("oadm.sample.ping", out var contributed));
        Assert.Same(core.Plugin, contributed.Owner);
        Assert.Same(core.Plugin.TaskPlugins[0], contributed.Plugin);

        Assert.True(_registry.TryGetTaskPlugin("oadm.sample.standalone", out var standalone));
        Assert.Null(standalone.Owner);
        Assert.True(standalone.Plugin.ShowInToolbar);

        // The contributed type is not registered a second time as a standalone task.
        Assert.Equal(2, _registry.TaskPlugins.Count);
    }

    [Fact]
    public async Task ContributedTaskRunsThroughTheEngineAndReachesItsOwner()
    {
        _loader.LoadPackage(SampleDirectory);
        var devices = new FakeDeviceRepository();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), _registry, devices, new FakeVapixClientFactory());

        var taskId = await engine.RunAsync("oadm.sample.ping", devices.AddMany(3), null, "o", CancellationToken.None);
        await engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TaskState.Done, (await engine.GetAsync(taskId, CancellationToken.None))!.State);
        var core = _registry.CorePlugins.Single().Plugin;
        Assert.Equal("3", await core.InvokeAsync("runs", null, CancellationToken.None));
    }

    [Fact]
    public void LoadFromRootsScansSubfoldersAndIsolatesBrokenPlugins()
    {
        CopySample("good");
        var broken = Directory.CreateDirectory(Path.Combine(_temp, "broken")).FullName;
        File.WriteAllText(Path.Combine(broken, "plugin.json"), "{ not json");
        var noDll = Directory.CreateDirectory(Path.Combine(_temp, "nodll")).FullName;
        File.WriteAllText(Path.Combine(noDll, "plugin.json"), """{"id":"x.nodll","version":"1.0.0","minSdkVersion":"0.1.0"}""");
        Directory.CreateDirectory(Path.Combine(_temp, "not-a-plugin"));

        _loader.LoadFromRoots([_temp, Path.Combine(_temp, "missing-root")]);

        Assert.Single(_loader.Packages);
        Assert.Single(_registry.CorePlugins);
        Assert.Equal(2, _registry.Errors.Count);
        Assert.Contains(_registry.Errors, e => e.Source == broken);
        Assert.Contains(_registry.Errors, e => e.Source == noDll && e.Message.Contains("Server.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsPluginsThatNeedANewerSdk()
    {
        var dir = CopySample("future");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"id":"oadm.sample","version":"1.0.0","minSdkVersion":"99.0.0"}""");

        Assert.Null(_loader.LoadPackage(dir));

        Assert.Empty(_registry.CorePlugins);
        Assert.Contains("99.0.0", Assert.Single(_registry.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePackageFromTwoFoldersLoadsOnce()
    {
        var first = CopySample("a");
        var second = CopySample("b");

        Assert.NotNull(_loader.LoadPackage(first));
        Assert.Null(_loader.LoadPackage(second));

        Assert.Single(_registry.CorePlugins);
        Assert.Single(_registry.Errors);
    }

    [Fact]
    public void RegistersPluginsCompiledIntoAHostAssembly()
    {
        var count = _loader.LoadFromAssembly(typeof(RestartTaskPlugin).Assembly);

        Assert.Equal(1, count);
        Assert.True(_registry.TryGetTaskPlugin("oadm.restart", out var restart));
        Assert.IsType<RestartTaskPlugin>(restart.Plugin);
        Assert.Null(restart.Owner);
        Assert.Equal("Oadm.Plugins.Restart.Server", restart.Origin.PackageId);
    }

    [Fact]
    public void RestartPluginIsDeployedForDevelopment()
    {
        var root = PluginPaths.Development(AppContext.BaseDirectory);
        Assert.NotNull(root);
        var dir = Path.Combine(root, "oadm.restart");
        Assert.True(File.Exists(Path.Combine(dir, "Oadm.Plugins.Restart.Server.dll")));
        Assert.False(File.Exists(Path.Combine(dir, "Oadm.Sdk.dll")));

        var package = _loader.LoadPackage(dir);

        Assert.NotNull(package);
        Assert.Empty(_registry.Errors);
        Assert.True(_registry.TryGetTaskPlugin("oadm.restart", out var restart));
        Assert.NotSame(typeof(RestartTaskPlugin), restart.Plugin.GetType()); // loaded in its own context
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.0.9", true)]
    [InlineData("0.1", true)]
    [InlineData("0.1.0-beta", true)]
    [InlineData("0.2.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void ChecksMinSdkVersion(string minSdk, bool compatible)
    {
        Assert.Equal(compatible, PluginLoader.IsSdkCompatible(minSdk));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var origin = new PluginOrigin("test", "1.0.0", null);
        Assert.True(_registry.RegisterTaskPlugin(new RestartTaskPlugin(), origin));
        Assert.False(_registry.RegisterTaskPlugin(new RestartTaskPlugin(), origin));
        Assert.Single(_registry.TaskPlugins);
        Assert.Single(_registry.Errors);
    }

    private string CopySample(string name)
    {
        var target = Path.Combine(_temp, name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(SampleDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }
}
