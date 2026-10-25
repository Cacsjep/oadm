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
}
