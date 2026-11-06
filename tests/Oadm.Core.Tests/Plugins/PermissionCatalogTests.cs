using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

/// <summary>Which tasks the role editor lists (docs/spec/security.md, Roles; docs/spec/plugins.md, ShowInMenus).</summary>
public sealed class PermissionCatalogTests
{
    [Fact]
    public void ATaskHiddenFromTheMenusListsOnlyThePermissionsItDeclares()
    {
        var registry = new PluginRegistry();
        Register(registry, new Task("t.menu", showInMenus: true));
        Register(registry, new Task("t.hidden", showInMenus: false));
        Register(registry, new Task("t.hidden-own", showInMenus: false, new PermissionDefinition("enroll", "Enroll", "Enroll certificates.")));

        var keys = PermissionCatalog.Build(registry).Entries.Select(e => e.Key).ToHashSet();

        Assert.Contains(PermissionKeys.Task("t.menu"), keys);
        Assert.DoesNotContain(PermissionKeys.Task("t.hidden"), keys); // its page's methods carry the rights
        Assert.Contains(PermissionKeys.Task("t.hidden-own", "enroll"), keys); // still grantable while it is hidden
    }

    /// <summary>
    /// A page task (hidden, no permissions of its own) has no key in the catalog: only a page of its own package starts it
    /// without the run keys; every other task and every other package keeps the role check.
    /// </summary>
    [Fact]
    public void OnlyAPageOfTheSamePackageStartsAPageTaskWithoutRunKeys()
    {
        var registry = new PluginRegistry();
        Register(registry, new Task("t.menu", showInMenus: true));
        Register(registry, new Task("t.hidden", showInMenus: false));
        Register(registry, new Task("t.hidden-own", showInMenus: false, new PermissionDefinition("enroll", "Enroll", "Enroll certificates.")));
        var none = PermissionSet.None;
        void Run(string task, string? package) =>
            Oadm.Core.Tasks.TaskRunPermissions.Require(registry, task, null, none, refuseUnknown: true, package);

        Run("t.hidden", "TEST.t.hidden"); // package ids compare without case

        Assert.Throws<DeviceAccessRefusedException>(() => Run("t.hidden", null));
        Assert.Throws<DeviceAccessRefusedException>(() => Run("t.hidden", "test.t.menu"));
        Assert.Throws<DeviceAccessRefusedException>(() => Run("t.menu", "test.t.menu"));
        Assert.Throws<DeviceAccessRefusedException>(() => Run("t.hidden-own", "test.t.hidden-own"));
        Assert.Throws<ArgumentException>(() => Run("t.unknown", "test.t.hidden"));
    }

    [Fact]
    public void PluginGroupsFollowTheRailOrderSoVisionComesRightAfterMonitoring()
    {
        var registry = new PluginRegistry();
        foreach (var (id, group) in new[] { ("c.ext", CorePluginGroup.Extensions), ("c.vision", CorePluginGroup.Vision), ("c.report", CorePluginGroup.Reporting), ("c.monitor", CorePluginGroup.Monitoring) })
        {
            Assert.True(registry.RegisterCorePlugin(new Core(id, group), new PluginOrigin("test." + id, "1.0.0", null)));
        }

        var groups = PermissionCatalog.Build(registry).Entries.Where(e => e.PackageId is not null).ToList();

        Assert.Equal(["c.monitor", "c.vision", "c.report", "c.ext"], groups.Select(e => e.Group).Distinct());
        Assert.True(groups.Zip(groups.Skip(1)).All(p => p.First.GroupOrder <= p.Second.GroupOrder), "GroupOrder follows the rail");
        Assert.Equal(CorePluginGroups.RailOrder(CorePluginGroup.Monitoring) + 1, CorePluginGroups.RailOrder(CorePluginGroup.Vision));
        Assert.Equal(CorePluginGroups.RailOrder(CorePluginGroup.Extensions) + 1, CorePluginGroups.RailOrder((CorePluginGroup)99)); // unknown: last
    }

    private static void Register(PluginRegistry registry, ITaskPlugin plugin) =>
        Assert.True(registry.RegisterTaskPlugin(plugin, new PluginOrigin("test." + plugin.Id, "1.0.0", null)));

    private sealed class Task(string id, bool showInMenus, params PermissionDefinition[] permissions) : ITaskPlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool ShowInMenus => showInMenus;

        public bool RequiresDialog => false;

        public IReadOnlyList<PermissionDefinition> Permissions => permissions;

        public bool CanRun(IDeviceInfo device) => true;

        public System.Threading.Tasks.Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
            System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class Core(string id, CorePluginGroup group) : ICorePlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public CorePluginGroup Group => group;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public System.Threading.Tasks.Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task StopAsync(CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<string?>(null);
    }
}
