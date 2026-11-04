using Oadm.Core.Compliance;
using Oadm.Core.Plugins;
using Oadm.Core.Tests.Compliance;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// Packages loaded while the server runs (docs/spec/add-ons.md rule 5): the registry reports them once, after the start's
/// loading, and the readers that follow Turn on and off rebuild.
/// </summary>
public sealed class RuntimePackageRegistryTests : IDisposable
{
    private readonly PluginRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly List<string> _raised = [];

    public RuntimePackageRegistryTests()
    {
        _loader = new PluginLoader(_registry);
        _registry.EnabledChanged += (_, _) => _raised.Add("enabled");
        _registry.PackageAdded += (_, e) => _raised.Add("added:" + e.PackageId);
    }

    private static string SampleDirectory =>
        TestRepository.DeployedPlugin(TestRepository.TestPlugins, "oadm.sample", "Oadm.TestPlugins.Sample.Server.dll");

    public void Dispose()
    {
        foreach (var package in _loader.Packages)
        {
            package.LoadContext.Unload();
        }
    }

    [Fact]
    public void AReaderOfTheRegistryThatFailsNeverUndoesALoadedPackage()
    {
        _registry.MarkStarted();
        _registry.PackageAdded += (_, _) => throw new InvalidOperationException("The reader failed.");

        Assert.NotNull(_loader.LoadPackage(SampleDirectory));

        Assert.True(_registry.HasPackage("oadm.sample"));
        Assert.Contains(_registry.Errors, e => e.Message.Contains("The reader failed.", StringComparison.Ordinal));
    }

    [Fact]
    public void APackageLoadedDuringTheStartIsNotReportedAsAdded()
    {
        Assert.NotNull(_loader.LoadPackage(SampleDirectory));

        Assert.False(_registry.IsStarted);
        Assert.Empty(_raised);
        Assert.False(_registry.PackageRegistered("oadm.sample")); // nothing before the start ended
    }

    [Fact]
    public void APackageLoadedAfterTheStartIsReportedOnceAfterItsPluginsAreRegistered()
    {
        _registry.MarkStarted();
        var seen = new List<int>();
        _registry.PackageAdded += (_, _) => seen.Add(_registry.AllTaskPlugins.Count);

        Assert.NotNull(_loader.LoadPackage(SampleDirectory));

        // The catalogues rebuild first (EnabledChanged), then the package is announced with every plugin registered.
        Assert.Equal(["enabled", "added:oadm.sample"], _raised);
        Assert.Equal([2], seen);
        Assert.True(_registry.HasPackage("oadm.sample"));

        // The same folder again is refused by the loader and reported nothing; a second report of the package neither.
        Assert.Null(_loader.LoadPackage(SampleDirectory));
        Assert.False(_registry.PackageRegistered("oadm.sample"));
        Assert.Equal(2, _raised.Count);
    }

    [Fact]
    public void APackageWithoutRegisteredPluginsIsNotReported()
    {
        _registry.MarkStarted();

        Assert.False(_registry.PackageRegistered("test.nothing"));

        Assert.Empty(_raised);
    }

    [Fact]
    public void TheComplianceTypesOfAnAddedPackageAppearAsOnTurnOn()
    {
        var catalog = new ComplianceCatalog(_registry);
        var before = catalog.Types.Count; // built and kept until the registry reports a change
        var changed = 0;
        catalog.Changed += (_, _) => changed++;
        _registry.MarkStarted();

        Assert.True(_registry.RegisterCorePlugin(new FakeCompliancePlugin(), new PluginOrigin("test.compliance", "1.0.0", null) { EnabledByDefault = false }));
        Assert.True(_registry.PackageRegistered("test.compliance"));

        Assert.Equal(1, changed);
        Assert.Equal(before + 2, catalog.Types.Count);
        Assert.All(catalog.Types.Where(t => t.Origin?.PackageId == "test.compliance"), t => Assert.False(t.Available));
        _registry.SetEnabled("test.compliance", true);
        Assert.All(catalog.Types.Where(t => t.Origin?.PackageId == "test.compliance"), t => Assert.True(t.Available));
    }
}
