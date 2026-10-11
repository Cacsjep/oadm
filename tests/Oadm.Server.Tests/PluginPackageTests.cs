using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Plugin packages turned on and off (Plugins page): the default of the manifest, live start / stop, menus, roles, audit, persistence.</summary>
public sealed class PluginPackageTests
{
    [Fact]
    public async Task APackageThatIsOffByDefaultStartsOnlyWhenAnAdministratorTurnsItOn()
    {
        await using var host = await TestServerHost.StartAsync();
        var core = new CountingCorePlugin("test.dashboard");
        var registry = host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(core, new PluginOrigin("test.dashboard", "1.0.0", null) { DisplayName = "Dashboard", EnabledByDefault = false });
        registry.RegisterTaskPlugin(new MenuTask("test.restart"), new PluginOrigin("test.restart", "1.0.0", null) { DisplayName = "Restart" });
        await host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);

        Assert.Equal(0, core.Starts);
        Assert.Empty((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins);
        var packages = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages;
        var dashboard = packages.Single(p => p.Id == "test.dashboard");
        Assert.Equal(("Dashboard", false, false, true), (dashboard.DisplayName, dashboard.Enabled, dashboard.EnabledByDefault, dashboard.HasPage));
        Assert.Equal(1, packages.Single(p => p.Id == "test.restart").MenuTaskCount);
        Assert.Equal("Extensions", packages.Single(p => p.Id == "test.restart").Group); // no core plugin: Extensions, like the rail
        var invoke = new Proto.InvokeRequest { PluginId = "test.dashboard", Method = "ping" };
        var off = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.InvokeAsync(invoke));
        Assert.Equal((StatusCode.FailedPrecondition, "The Dashboard plugin is turned off."), (off.StatusCode, off.Status.Detail));

        // Operators see the list but cannot change it.
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));
        Assert.Equal(2, (await op.ListPackagesAsync(new Proto.Empty())).Packages.Count);
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "test.dashboard", Enabled = true }));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var reply = await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "test.dashboard", Enabled = true });
        Assert.True(reply.Packages.Single(p => p.Id == "test.dashboard").Enabled);
        Assert.Equal(1, core.Starts);
        Assert.Equal("test.dashboard", Assert.Single((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins).Id);
        Assert.Equal("pong", (await host.Plugins.InvokeAsync(invoke)).PayloadJson);

        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "nope", Enabled = true }));
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Single(e => e.Action == AuditActions.PluginTurnedOn);
        Assert.Equal("Dashboard", audit.Target);

        // The choice is stored: a restart (a new registry state from the setting) keeps it on.
        registry.ApplyEnabledStates(new Dictionary<string, bool>());
        Assert.False(registry.IsEnabled("test.dashboard"));
        await host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        Assert.True(registry.IsEnabled("test.dashboard"));
    }

    [Fact]
    public async Task APackageReportsTheRailGroupOfItsCorePlugin()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(new CountingCorePlugin("test.ntp") { Group = CorePluginGroup.Servers }, new PluginOrigin("test.ntp", "1.0.0", null) { DisplayName = "NTP" });
        registry.RegisterCorePlugin(new CountingCorePlugin("test.monitor") { Group = CorePluginGroup.Monitoring }, new PluginOrigin("test.monitor", "1.0.0", null) { DisplayName = "Monitor" });

        var packages = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages;

        Assert.Equal("Servers", packages.Single(p => p.Id == "test.ntp").Group);
        Assert.Equal("Monitoring", packages.Single(p => p.Id == "test.monitor").Group);
    }

    [Fact]
    public async Task TurningAPackageOffStopsItsCorePluginAndRemovesItsTasksFromTheMenus()
    {
        await using var host = await TestServerHost.StartAsync();
        var core = new CountingCorePlugin("test.pki", new MenuTask("test.pki.deploy"));
        host.Get<PluginRegistry>().RegisterCorePlugin(core, new PluginOrigin("test.pki", "1.0.0", null) { DisplayName = "PKI" });
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        Assert.Contains((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.pki.deploy");

        await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "test.pki", Enabled = false });

        Assert.Equal(1, core.Stops);
        Assert.DoesNotContain((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.pki.deploy");
        Assert.Empty((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins);
        Assert.Contains((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.PluginTurnedOff && e.Target == "PKI");

        await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "test.pki", Enabled = true });
        Assert.Equal(2, core.Starts);
        Assert.Contains((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.pki.deploy");
    }

    [Fact]
    public async Task APackageThatIsAlwaysOnCannotBeTurnedOffAndIgnoresAStoredChoice()
    {
        await using var host = await TestServerHost.StartAsync();
        var core = new CountingCorePlugin("test.pki", new MenuTask("test.pki.deploy"));
        var registry = host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(core, new PluginOrigin("test.pki", "1.0.0", null)
        {
            DisplayName = "PKI",
            AlwaysOn = true,
            Description = "Issues device certificates.",
        });

        // A choice stored before the package became always on (or edited by hand) is ignored.
        registry.ApplyEnabledStates(new Dictionary<string, bool> { ["test.pki"] = false });
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        Assert.Equal(1, core.Starts);
        Assert.True(registry.IsEnabled("test.pki"));

        var package = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.Single(p => p.Id == "test.pki");
        Assert.Equal((true, true, "Issues device certificates."), (package.Enabled, package.AlwaysOn, package.Description));

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "test.pki", Enabled = false }));
        Assert.Equal((StatusCode.FailedPrecondition, "PKI is always on."), (refused.StatusCode, refused.Status.Detail));
        Assert.Equal(0, core.Stops);
        Assert.Contains((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.pki.deploy");
        Assert.DoesNotContain((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.PluginTurnedOff);
    }

    [Fact]
    public async Task ACallCancelledDuringTheChangeStillStoresItAndStartsThePlugin()
    {
        await using var host = await TestServerHost.StartAsync();
        using var cts = new CancellationTokenSource();
        var core = new CountingCorePlugin("test.dashboard") { OnStart = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); } };
        var registry = host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(core, new PluginOrigin("test.dashboard", "1.0.0", null) { DisplayName = "Dashboard", EnabledByDefault = false });
        var activation = host.Get<PluginActivation>();
        await activation.LoadAsync(CancellationToken.None);

        // The caller goes away while the plugin starts: the change still completes everywhere.
        var state = await activation.SetEnabledAsync("test.dashboard", true, cts.Token);

        Assert.True(state.Enabled);
        Assert.Equal(1, core.Starts);
        Assert.Equal("test.dashboard", Assert.Single((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins).Id);
        Assert.Equal("pong", (await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = "test.dashboard", Method = "ping" })).PayloadJson);
        registry.ApplyEnabledStates(new Dictionary<string, bool>());
        await activation.LoadAsync(CancellationToken.None);
        Assert.True(registry.IsEnabled("test.dashboard"));

        // A call cancelled before it got its turn changes nothing.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.SetEnabledAsync("test.dashboard", false, cts.Token));
        Assert.True(registry.IsEnabled("test.dashboard"));
        Assert.Equal(0, core.Stops);
    }

    [Fact]
    public void TheBundledManifestsTurnTheHardeningScanAndTheImageHealthDashboardOffByDefault()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var manifests = Directory.GetFiles(Path.Combine(root, "plugins"), PluginLoader.ManifestFileName, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(PluginLoader.ReadManifest)
            .ToList();
        Assert.NotEmpty(manifests);
        Assert.Equal(["oadm.device-triggers", "oadm.hardening-scan", "oadm.image-health", "oadm.io-monitor", "oadm.metadata-monitor"], manifests.Where(m => !m.EnabledByDefault).Select(m => m.Id).Order());

        // Core functionality is always on (user decision 2026-10-10); every bundled plugin has a description.
        Assert.Equal(
            ["oadm.acap", "oadm.datetime", "oadm.device-config", "oadm.firmware", "oadm.network", "oadm.notifications", "oadm.outputs", "oadm.pki", "oadm.restart", "oadm.system-report", "oadm.users"],
            manifests.Where(m => m.AlwaysOn).Select(m => m.Id).Order());
        Assert.All(manifests, m => Assert.False(string.IsNullOrWhiteSpace(m.Description), m.Id));
    }

    private sealed class CountingCorePlugin(string id, params ITaskPlugin[] tasks) : ICorePlugin
    {
        public int Starts;

        public int Stops;

        /// <summary>Runs inside <see cref="StartAsync"/> with its token.</summary>
        public Action<CancellationToken>? OnStart { get; init; }

        public string Id => id;

        public CorePluginGroup Group { get; init; } = CorePluginGroup.Extensions;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => tasks;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            OnStart?.Invoke(ct);
            Starts++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>("pong");
    }

    private sealed class MenuTask(string id) : ITaskPlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
