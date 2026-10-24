using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The EST and 802.1X servers as parts of PKI (docs/spec/pki.md "One plugin"): one package with three core plugins whose
/// ids, permissions, events and tasks stay those of OADM 4.1, folders of 4.1 skipped, the on/off choices taken over once.
/// </summary>
#pragma warning disable CA1001 // disposed in DisposeAsync (IAsyncLifetime)
public sealed class MergedPluginsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "oadm-merged-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<PluginLoader> _loaders = [];
    private TestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        foreach (var package in _loaders.SelectMany(l => l.Packages))
        {
            package.LoadContext.Unload();
        }

        await _db.DisposeAsync();
        TestDatabase.DeleteDirectory(_temp);
    }

    private static string PkiDirectory => TestRepository.DeployedPlugin(TestRepository.DevelopmentPlugins, "oadm.pki", "Oadm.Plugins.Pki.Server.dll");

    private static string SampleDirectory => TestRepository.DeployedPlugin(TestRepository.TestPlugins, "oadm.sample", "Oadm.TestPlugins.Sample.Server.dll");

    private PluginLoader Loader(PluginRegistry registry, ILogger<PluginLoader>? logger = null)
    {
        var loader = new PluginLoader(registry, logger);
        _loaders.Add(loader);
        return loader;
    }

    [Fact]
    public void ThePkiPackageHoldsThreeCorePluginsWithTheIdsOf41()
    {
        var registry = new PluginRegistry();
        var package = Loader(registry).LoadPackage(PkiDirectory);

        Assert.NotNull(package);
        Assert.True(registry.Errors.Count == 0, string.Join("; ", registry.Errors.Select(e => e.Message)));
        // One package, three rail pages in the order of 4.1 (EST server, PKI, 802.1X server), all of the PKI package.
        Assert.Equal(["oadm.est", "oadm.pki", "oadm.radius"], registry.AllCorePlugins.Select(c => c.Id));
        Assert.All(registry.AllCorePlugins, c => Assert.Equal("oadm.pki", c.Origin.PackageId));
        Assert.Equal(["EST server", "PKI", "802.1X server"], registry.AllCorePlugins.Select(c => c.Plugin.DisplayName));
        Assert.All(registry.AllCorePlugins, c => Assert.True(c.Plugin.HasPage));
        Assert.All(registry.AllCorePlugins, c => Assert.Equal(CorePluginGroup.Security, c.Plugin.Group));
        Assert.Equal(["lock", "shield", "lock"], registry.AllCorePlugins.Select(c => c.Plugin.IconKey));
        var state = Assert.Single(registry.Packages);
        Assert.True(state.Origin.AlwaysOn);
        Assert.False(state.Origin.Pro); // PKI is free; only its two servers need OADM PRO
        Assert.Equal(["udp 1812", "tcp 9454"], state.Origin.SitePorts.Select(p => $"{p.Protocol} {p.Port}".ToLowerInvariant()));

        // The task of the EST server keeps its id and its owner; the PKI tasks stay PKI's.
        Assert.True(registry.TryGetTaskPlugin("oadm.est.setup", out var setup));
        Assert.Equal("oadm.est", setup.Owner?.Id);
        Assert.True(registry.TryGetTaskPlugin("oadm.pki.https-enable", out var https));
        Assert.Equal("oadm.pki", https.Owner?.Id);

        // Event rule events keep their ids, so stored rules keep working.
        var events = registry.AllEventTypes.Select(e => e.Type.Id).ToHashSet();
        Assert.Superset(new HashSet<string> { "oadm.est.enrolled", "oadm.est.refused", "oadm.est.notrenewed", "oadm.radius.authenticated", "oadm.radius.failed" }, events);

        // Permissions keep their keys, so stored roles keep working ("<core plugin id>:<key>", "task:<id>").
        var keys = PermissionCatalog.Build(registry).Entries.Select(e => e.Key).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "oadm.est:show", "oadm.est:manage", "oadm.radius:show", "oadm.radius:manage", "oadm.pki:show", "oadm.pki:manage",
            "oadm.pki:trustServer", "task:oadm.est.setup:enroll",
        }, keys);
        Assert.All(PermissionCatalog.Build(registry).Entries.Where(e => e.Key.StartsWith("oadm.est:", StringComparison.Ordinal) || e.Key.StartsWith("oadm.radius:", StringComparison.Ordinal)),
            e => Assert.Equal("oadm.pki", e.PackageId));

        // The source-available licenses ship next to their parts; the SDK and BouncyCastle come from the host.
        Assert.True(File.Exists(Path.Combine(PkiDirectory, "Est", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(PkiDirectory, "Radius", "LICENSE")));
        Assert.False(File.Exists(Path.Combine(PkiDirectory, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(PkiDirectory, "Oadm.Sdk.dll")));
        Assert.False(File.Exists(Path.Combine(PkiDirectory, "BouncyCastle.Cryptography.dll")));
    }

    [Theory]
    [InlineData("oadm.est")]
    [InlineData("oadm.radius")]
    [InlineData("OADM.Radius")]
    public void AFolderOfAMergedPluginLeftFrom41IsSkipped(string id)
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "plugins")).FullName;
        var old = CopySample(root, id);
        File.WriteAllText(Path.Combine(old, "plugin.json"), $$"""{"id":"{{id}}","version":"0.1.0","minSdkVersion":"0.4.0","pro":true}""");
        var logger = new ListLogger<PluginLoader>();
        var registry = new PluginRegistry();
        var loader = Loader(registry, logger);

        loader.LoadFromRoots([root]);

        Assert.Empty(loader.Packages);
        Assert.Empty(registry.AllCorePlugins);
        Assert.True(registry.Errors.Count == 0, string.Join("; ", registry.Errors.Select(e => e.Message))); // not an error: the installers remove the folder
        var line = Assert.Single(logger.Entries, e => e.Message.Contains("Merged into PKI", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(old, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftoverFoldersNeverTakeTheIdsOfPki()
    {
        // "oadm.est" sorts before "oadm.pki": a leftover folder would load first and take the core plugin id oadm.est.
        var root = Directory.CreateDirectory(Path.Combine(_temp, "installed")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(root, "oadm.est")).FullName;
        foreach (var file in Directory.GetFiles(PkiDirectory))
        {
            File.Copy(file, Path.Combine(old, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(old, "plugin.json"), """{"id":"oadm.est","version":"0.1.0","minSdkVersion":"0.4.0","pro":true}""");
        var registry = new PluginRegistry();
        var loader = Loader(registry, NullLogger<PluginLoader>.Instance);

        loader.LoadFromRoots([root]);
        Assert.NotNull(loader.LoadPackage(PkiDirectory));

        Assert.True(registry.Errors.Count == 0, string.Join("; ", registry.Errors.Select(e => e.Message)));
        Assert.Equal("oadm.pki", registry.AllCorePlugins.Single(c => c.Id == "oadm.est").Origin.PackageId);
        Assert.DoesNotContain(loader.Packages, p => p.Directory == old);
    }

    [Fact]
    public void TheMergedIdsAreKnown()
    {
        Assert.Equal("oadm.pki", MergedPlugins.MergedInto("oadm.est"));
        Assert.Equal("oadm.pki", MergedPlugins.MergedInto("oadm.radius"));
        Assert.Null(MergedPlugins.MergedInto("oadm.pki"));
        Assert.Null(MergedPlugins.MergedInto("acme.est"));
        Assert.Null(MergedPlugins.MergedInto(null));
    }

    [Fact]
    public async Task TheChoicesOf41BecomeTheServersOwnOnceAndEverythingElseStays()
    {
        var settings = _db.Get<ServerSettingsStore>();
        var fixture = await LoadFixtureAsync(settings);

        var taken = await MergedPlugins.TakeOverAsync(settings, "Plugins.Enabled", NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["oadm.est", "oadm.radius"], taken.Order());
        Assert.Equal("true", await settings.GetJsonAsync("Plugin:oadm.est:serverOn", CancellationToken.None)); // on when its plugin was on
        Assert.Equal("false", await settings.GetJsonAsync("Plugin:oadm.radius:serverOn", CancellationToken.None));
        Assert.Equal(new Dictionary<string, bool> { ["oadm.live-view"] = true }, await EnabledAsync(settings));
        // Settings and records stay under the core plugin ids: they are PKI's now without a copy.
        foreach (var (key, value) in fixture.Where(f => f.Key.StartsWith("Plugin:", StringComparison.Ordinal)))
        {
            Assert.Equal(value, await settings.GetJsonAsync(key, CancellationToken.None));
        }

        // Every later start: nothing more to take over, nothing changes.
        Assert.Empty(await MergedPlugins.TakeOverAsync(settings, "Plugins.Enabled", NullLogger.Instance, CancellationToken.None));
        Assert.Equal("true", await settings.GetJsonAsync("Plugin:oadm.est:serverOn", CancellationToken.None));
        Assert.Equal(new Dictionary<string, bool> { ["oadm.live-view"] = true }, await EnabledAsync(settings));
    }

    [Fact]
    public async Task AChoiceMadeSinceWinsAndAStopHalfwayIsFinishedNextTime()
    {
        var settings = _db.Get<ServerSettingsStore>();
        // A start that stopped after the setting was written, before the entry left Plugins.Enabled; or a choice made since.
        await settings.SetJsonAsync("Plugins.Enabled", """{"oadm.est":true,"oadm.radius":true}""", CancellationToken.None);
        await settings.SetJsonAsync("Plugin:oadm.est:serverOn", "false", CancellationToken.None);

        var taken = await MergedPlugins.TakeOverAsync(settings, "Plugins.Enabled", NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["oadm.est", "oadm.radius"], taken.Order());
        Assert.Equal("false", await settings.GetJsonAsync("Plugin:oadm.est:serverOn", CancellationToken.None));
        Assert.Equal("true", await settings.GetJsonAsync("Plugin:oadm.radius:serverOn", CancellationToken.None));
        Assert.Empty(await EnabledAsync(settings));
    }

    [Fact]
    public async Task WithoutAChoiceOf41NothingIsWrittenAndTheServersStayOff()
    {
        var settings = _db.Get<ServerSettingsStore>();
        Assert.Empty(await MergedPlugins.TakeOverAsync(settings, "Plugins.Enabled", NullLogger.Instance, CancellationToken.None));
        await settings.SetJsonAsync("Plugins.Enabled", """{"oadm.live-view":false}""", CancellationToken.None);
        Assert.Empty(await MergedPlugins.TakeOverAsync(settings, "Plugins.Enabled", NullLogger.Instance, CancellationToken.None));

        Assert.Null(await settings.GetJsonAsync("Plugin:oadm.est:serverOn", CancellationToken.None));
        Assert.Null(await settings.GetJsonAsync("Plugin:oadm.radius:serverOn", CancellationToken.None));
        Assert.Equal(new Dictionary<string, bool> { ["oadm.live-view"] = false }, await EnabledAsync(settings));
    }

    private static async Task<Dictionary<string, string>> LoadFixtureAsync(ServerSettingsStore settings)
    {
        var rows = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plugins", "settings-4.1.json")))!;
        rows.Remove("_comment");
        foreach (var (key, value) in rows)
        {
            await settings.SetJsonAsync(key, value, CancellationToken.None);
        }

        return rows;
    }

    private static async Task<Dictionary<string, bool>> EnabledAsync(ServerSettingsStore settings) =>
        JsonSerializer.Deserialize<Dictionary<string, bool>>((await settings.GetJsonAsync("Plugins.Enabled", CancellationToken.None))!)!;

    private static string CopySample(string root, string name)
    {
        var target = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        foreach (var file in Directory.GetFiles(SampleDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
