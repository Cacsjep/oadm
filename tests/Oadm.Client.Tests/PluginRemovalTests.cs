using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Contracts.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Client.Tests;

/// <summary>
/// docs/spec/plugin-removal.md, the client: a bundled folder the shipped manifest does not list is skipped and deleted (only
/// skipped inside the macOS app bundle, never touched in the data folder), and the plugin settings of a formerly shipped id
/// go once.
/// </summary>
public sealed class PluginRemovalTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("oadm-client-removal-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SampleDirectory => TestRepository.DeployedPlugin(TestRepository.TestPlugins, "oadm.sample", "Oadm.TestPlugins.Sample.Client.dll");

    private static void CopySample(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(SampleDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private static void Manifest(string root, params string[] ids) =>
        File.WriteAllText(Path.Combine(root, BundledPlugins.FileName), "{ \"plugins\": [" + string.Join(", ", ids.Select(i => '"' + i + '"')) + "] }");

    [Fact]
    public void A_folder_the_manifest_does_not_list_is_skipped_and_deleted_and_a_listed_one_loads()
    {
        var bundled = Directory.CreateDirectory(Path.Combine(_temp, "app", "plugins")).FullName;
        CopySample(Path.Combine(bundled, "oadm.sample"));
        Manifest(bundled, "oadm.other");

        var loader = new ClientPluginLoader([bundled], NullLogger<ClientPluginLoader>.Instance);

        Assert.False(loader.HasPackage("oadm.sample"));
        Assert.False(Directory.Exists(Path.Combine(bundled, "oadm.sample")));
        Assert.NotNull(loader.BundledIds);
        Assert.Empty(loader.BundledIds);

        CopySample(Path.Combine(bundled, "oadm.sample"));
        Manifest(bundled, "oadm.sample");
        var listed = new ClientPluginLoader([bundled], NullLogger<ClientPluginLoader>.Instance);
        Assert.True(listed.HasPackage("oadm.sample"));
        Assert.Contains("oadm.sample", listed.BundledIds!);
    }

    [Fact]
    public void Inside_the_app_bundle_a_folder_is_only_skipped_and_the_data_folder_is_never_touched()
    {
        var resources = Directory.CreateDirectory(Path.Combine(_temp, "OADM.app", "Contents", "Resources", "plugins")).FullName;
        CopySample(Path.Combine(resources, "oadm.sample"));
        Manifest(resources);
        var installed = Directory.CreateDirectory(Path.Combine(_temp, "data", "plugins")).FullName;
        CopySample(Path.Combine(installed, "oadm.sample"));
        Manifest(installed);

        var inBundle = new ClientPluginLoader([resources], NullLogger<ClientPluginLoader>.Instance);
        Assert.False(inBundle.HasPackage("oadm.sample"));
        Assert.True(Directory.Exists(Path.Combine(resources, "oadm.sample")));

        var data = new ClientPluginLoader([installed], NullLogger<ClientPluginLoader>.Instance, installedRoot: installed);
        Assert.True(data.HasPackage("oadm.sample"));
        Assert.Null(data.BundledIds); // no bundled root: a development run
    }

    [Fact]
    public void The_settings_of_a_formerly_shipped_id_go_once_and_nothing_goes_on_a_load_failure()
    {
        var store = new InMemoryClientSettingsStore();
        store.Current.BundledPlugins = ["oadm.gone", "oadm.kept"];
        store.Current.PluginSettings["oadm.gone/choice"] = "1";
        store.Current.PluginSettings["oadm.kept/choice"] = "2";
        store.Current.PluginSettings["oadm.gone-other/choice"] = "3";
        HashSet<string> known = new(["oadm.kept", "oadm.gone-other"], StringComparer.OrdinalIgnoreCase);
        HashSet<string> bundled = new(["oadm.kept", "oadm.new"], StringComparer.OrdinalIgnoreCase);

        Assert.Empty(RemovedClientPlugins.Clean(store, bundled, known, loadFailures: true));
        Assert.Empty(RemovedClientPlugins.Clean(store, null, known, loadFailures: false));
        Assert.Equal(3, store.Current.PluginSettings.Count);

        Assert.Equal(["oadm.gone"], RemovedClientPlugins.Clean(store, bundled, known, loadFailures: false));
        Assert.Equal(["oadm.gone-other/choice", "oadm.kept/choice"], store.Current.PluginSettings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["oadm.kept", "oadm.new"], store.Current.BundledPlugins);
        Assert.Empty(RemovedClientPlugins.Clean(store, bundled, known, loadFailures: false));
    }
}
