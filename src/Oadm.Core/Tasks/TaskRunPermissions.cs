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
    /// to the runner (another runner than the engine, in tests and tools).
    /// </summary>
    public static void Require(PluginRegistry registry, string pluginId, string? payloadJson, PermissionSet permissions, bool refuseUnknown)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (registry.TryGetTaskPlugin(pluginId, out var task))
        {
            Require(task.Plugin, payloadJson, permissions, () => PermissionCatalog.Build(registry));
        }
        else if (refuseUnknown)
        {
            throw new ArgumentException(UnknownPlugin(pluginId), nameof(pluginId));
        }
    }

    /// <summary>The answer for a task plugin id that is unknown or turned off.</summary>
    public static string UnknownPlugin(string pluginId) => $"Unknown task plugin '{pluginId}'.";
}
