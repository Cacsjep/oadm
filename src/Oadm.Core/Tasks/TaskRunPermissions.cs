using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Core.Tasks;

/// <summary>
/// The role check of a task run for a user (docs/spec/security.md, Users and roles): every permission the run needs
/// (<see cref="PermissionCatalog.RunKeys"/> for its payload). The one check of gRPC <c>TaskService.Run</c> and of
/// <c>call.Tasks.RunAsync</c> in a core plugin's server part, before any device is looked up or any task created.
/// </summary>
public static class TaskRunPermissions
{
    /// <summary>The first permission of the run <paramref name="permissions"/> lacks, else null.</summary>
    public static string? Missing(ITaskPlugin plugin, string? payloadJson, PermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(permissions);
        return PermissionCatalog.RunKeys(plugin, payloadJson).FirstOrDefault(key => !permissions.Has(key));
    }

    /// <summary>
    /// <see cref="DeviceAccessRefusedException"/> with <see cref="PermissionCatalog.DeniedMessage"/> ("Your role does not
    /// allow: Remove.") when the run needs a permission <paramref name="permissions"/> lacks; the host answers
    /// PERMISSION_DENIED with that text. <paramref name="catalog"/> is read only for a refusal.
    /// </summary>
    public static void Require(ITaskPlugin plugin, string? payloadJson, PermissionSet permissions, Func<PermissionCatalog> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (Missing(plugin, payloadJson, permissions) is { } key)
        {
            throw new DeviceAccessRefusedException(catalog().DeniedMessage(key));
        }
    }

    /// <summary>
    /// <see cref="Require(ITaskPlugin, string?, PermissionSet, Func{PermissionCatalog})"/> for a plugin id of
    /// <paramref name="registry"/>, the refusal named by the catalog of every loaded plugin (the names the matrix shows). An
    /// id the registry does not find: <see cref="ArgumentException"/> (<see cref="UnknownPlugin"/>) with
    /// <paramref name="refuseUnknown"/> (the engine's runner: a plugin turned on meanwhile never runs unchecked), else left
    /// to the runner (another runner than the engine, in tests and tools). <paramref name="pagePackageId"/>: the package of
    /// the core plugin whose page method (or job) runs; its own page tasks need no run keys (<see cref="StartsOwnPageTask"/>).
    /// </summary>
    public static void Require(PluginRegistry registry, string pluginId, string? payloadJson, PermissionSet permissions, bool refuseUnknown, string? pagePackageId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (registry.TryGetTaskPlugin(pluginId, out var task))
        {
            if (!StartsOwnPageTask(task, pagePackageId))
            {
                Require(task.Plugin, payloadJson, permissions, () => PermissionCatalog.Build(registry));
            }
        }
        else if (refuseUnknown)
        {
            throw new ArgumentException(UnknownPlugin(pluginId), nameof(pluginId));
        }
    }

    /// <summary>The refusal of <c>TaskService.Run</c> for a page task (<see cref="IsPageTask"/>).</summary>
    public const string PageTaskOnly = "This task starts only from its page.";

    /// <summary>
    /// A page task: hidden from the menus (<see cref="ITaskPlugin.ShowInMenus"/> false) without permissions of its own, so
    /// the role editor lists no key for it (<see cref="PermissionCatalog.Build"/>) and no role can hold one. Only a page of
    /// its own package starts it (<see cref="StartsOwnPageTask"/>); <c>TaskService.Run</c> refuses it for every caller. A
    /// plugin whose answer throws counts as hidden (like the menu) with no permissions.
    /// </summary>
    public static bool IsPageTask(ITaskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        bool shown;
        try
        {
            shown = plugin.ShowInMenus;
        }
#pragma warning disable CA1031 // A plugin with a throwing property is not in the menus.
        catch (Exception)
#pragma warning restore CA1031
        {
            shown = false;
        }

        return !shown && PermissionCatalog.DeclaredOf(plugin).Count == 0;
    }

    /// <summary>
    /// True when a page method of the package <paramref name="pagePackageId"/> starts a page task of that same package
    /// through the user's call: the host checked the method's permission before the plugin ran, and that is the check
    /// (the run keys are not required). Every other run needs its keys.
    /// </summary>
    public static bool StartsOwnPageTask(RegisteredTaskPlugin task, string? pagePackageId)
    {
        ArgumentNullException.ThrowIfNull(task);
        return pagePackageId is not null
            && string.Equals(task.Origin.PackageId, pagePackageId, StringComparison.OrdinalIgnoreCase)
            && IsPageTask(task.Plugin);
    }

    /// <summary>The answer for a task plugin id that is unknown or turned off.</summary>
    public static string UnknownPlugin(string pluginId) => $"Unknown task plugin '{pluginId}'.";
}
