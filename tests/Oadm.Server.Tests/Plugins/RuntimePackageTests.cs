using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Plugins;

/// <summary>
/// A package loaded while the server runs (an add-on installed, docs/spec/add-ons.md rule 5) is a package that was there at
/// the start but off: the Plugins page lists it, its permissions join the roles, its events and actions are in the catalogue
/// as not available, and Turn on starts it like any other. Nothing else changes.
/// </summary>
public sealed class RuntimePackageTests
{
    private const string AddOnId = "test.addon";

    [Fact]
    public async Task AnAddedPackageIsOffAndTurnsOnLikeAPackageOfTheStart()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        var other = new AddOnPlugin("test.other", "test.other.task");
        registry.RegisterCorePlugin(other, new PluginOrigin("test.other", "1.0.0", null) { DisplayName = "Other" });
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        var catalogBefore = host.Get<RoleStore>().Catalog.Entries.Select(e => e.Key).ToList();
        var rolesChanged = 0;
        host.Get<RoleStore>().Changed += (_, _) => rolesChanged++;

        var addOn = Add(registry, enabledByDefault: false);
        await host.Get<PluginActivation>().AddedPackagesFollowed;

        // Listed and off: no page, no menu entry, its events and actions not available; the plugin did not start.
        var package = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.Single(p => p.Id == AddOnId);
        Assert.Equal(("Add-on", false, true, 1, "Monitoring"), (package.DisplayName, package.Enabled, package.HasPage, package.MenuTaskCount, package.Group));
        Assert.DoesNotContain((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == AddOnId);
        Assert.DoesNotContain((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.addon.scan");
        var events = new Proto.EventService.EventServiceClient(host.Invoker);
        var catalog = await events.GetCatalogAsync(new Proto.Empty());
        Assert.False(catalog.EventTypes.Single(t => t.Id == "test.addon.read").Available);
        Assert.False(catalog.Actions.Single(a => a.Id == "test.addon.send").Available);
        Assert.Equal(0, addOn.Starts);

        // The roles know its permissions with the defaults of a new permission, as at a start.
        var roles = host.Get<RoleStore>();
        Assert.Equal(PermissionCatalog.Build(registry).Entries.Select(e => e.Key), roles.Catalog.Entries.Select(e => e.Key));
        Assert.Equal(
            ["task:test.addon.scan", "test.addon:read", "test.addon:show"],
            roles.Catalog.Entries.Select(e => e.Key).Except(catalogBefore).Where(k => k.Contains(AddOnId, StringComparison.Ordinal)).Order());
        var operatorRights = await roles.PermissionsOfAsync(BuiltInRoles.OperatorId, CancellationToken.None);
        Assert.True(operatorRights.Has("test.addon:show"));
        Assert.True(operatorRights.Has("task:test.addon.scan"));
        Assert.False(operatorRights.Has("test.addon:read")); // declared off for operators
        Assert.Equal(1, rolesChanged); // the clients' sessions get the new rights through the role stream

        // Nothing else changed: the other package keeps running and its state.
        Assert.Equal((1, 0), (other.Starts, other.Stops));
        Assert.True(registry.IsEnabled("test.other"));

        // Turn on: the existing path starts it, its page, task, events and actions are there.
        var reply = await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = AddOnId, Enabled = true });
        Assert.True(reply.Packages.Single(p => p.Id == AddOnId).Enabled);
        Assert.Equal(1, addOn.Starts);
        var op = await host.InvokerForUserAsync("operator1", UserRole.Operator);
        Assert.Contains((await new Proto.PluginService.PluginServiceClient(op).ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == AddOnId);
        Assert.Contains((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == "test.addon.scan");
        catalog = await events.GetCatalogAsync(new Proto.Empty());
        Assert.True(catalog.EventTypes.Single(t => t.Id == "test.addon.read").Available);
        Assert.True(catalog.Actions.Single(a => a.Id == "test.addon.send").Available);
        Assert.Equal("pong", (await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = AddOnId, Method = "ping" })).PayloadJson);
        Assert.Equal((1, 0), (other.Starts, other.Stops));
    }

    [Fact]
    public async Task AnAddedPackageThatIsOnByItsChoiceStartsAtOnce()
    {
        // Like a start: the stored choice (or the manifest default) decides; an add-on is off by default.
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();

        var addOn = Add(registry, enabledByDefault: true);
        await host.Get<PluginActivation>().AddedPackagesFollowed;

        Assert.Equal(1, addOn.Starts);
        Assert.Contains((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == AddOnId);
    }

    [Fact]
    public async Task AnAddedPackageWithATrialStartsItsTrialAsAtAStart()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        var (_, publicKey) = LicenseFile.CreateKeys();

        Add(registry, enabledByDefault: false, new PluginLicenseManifest(publicKey, ["read"], 30));
        await host.Get<PluginActivation>().AddedPackagesFollowed;

        var package = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.Single(p => p.Id == AddOnId);
        Assert.Equal(Proto.PluginLicenseStatus.PluginLicenseTrial, package.License.Status);
        Assert.Contains(AddOnId, await host.Get<Core.Settings.ServerSettingsStore>().GetJsonAsync(PluginLicenses.TrialsKey, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageReportedTwiceIsFollowedOnce()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        Add(registry, enabledByDefault: false);
        var added = 0;
        registry.PackageAdded += (_, _) => added++;

        Assert.False(registry.PackageRegistered(AddOnId));
        Assert.False(registry.PackageRegistered("test.unknown"));

        Assert.Equal(0, added);
    }

    /// <summary>Registers the add-on package the way <see cref="PluginLoader.LoadPackage"/> does while the server runs.</summary>
    private static AddOnPlugin Add(PluginRegistry registry, bool enabledByDefault, PluginLicenseManifest? license = null)
    {
        var plugin = new AddOnPlugin(AddOnId, "test.addon.scan")
        {
            Group = CorePluginGroup.Monitoring,
            Events = [new EventTypeInfo("test.addon.read", "Text read", "Add-on", EventKind.Stateless)],
            Actions = [new SendAction()],
            Declared = [new PermissionDefinition("read", "Read", "Read texts.") { OperatorDefault = false }],
        };
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(AddOnId, "1.0.0", null)
        {
            DisplayName = "Add-on",
            EnabledByDefault = enabledByDefault,
            License = license,
        }));
        Assert.True(registry.PackageRegistered(AddOnId));
        return plugin;
    }

    private sealed class AddOnPlugin(string id, string taskId) : ICorePlugin, IEventSource, IActionSource
    {
        public int Starts;

        public int Stops;

        public string Id => id;

        public CorePluginGroup Group { get; init; } = CorePluginGroup.Extensions;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [new MenuTask(taskId)];

        public IReadOnlyList<EventTypeInfo> EventTypes => Events;

        public IReadOnlyList<EventTypeInfo> Events { get; init; } = [];

        public IReadOnlyList<IActionPlugin> ActionPlugins => Actions;

        public IReadOnlyList<IActionPlugin> Actions { get; init; } = [];

        public IReadOnlyList<PermissionDefinition> Declared { get; init; } = [];

        public IReadOnlyList<PermissionDefinition> Permissions => Declared;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Stops);
            return Task.CompletedTask;
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>("pong");
    }

    private sealed class SendAction : IActionPlugin
    {
        public string Id => "test.addon.send";

        public string DisplayName => "Send text";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
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
