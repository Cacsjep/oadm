using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>One row of the permission matrix.</summary>
/// <param name="Key">"devices.addRemove", "oadm.dhcp-server:manage", "task:oadm.users:remove".</param>
/// <param name="Group">Group header in the matrix: "Devices", "Tasks", "Event rules" or the plugin's name.</param>
/// <param name="GroupOrder">Order of the group (host groups first, then plugins in rail order).</param>
/// <param name="PackageId">The plugin package the permission belongs to; null for host permissions.</param>
public sealed record PermissionEntry(string Key, string Name, string Description, string Group, int GroupOrder, string? PackageId)
{
    public bool OperatorDefault { get; init; }

    public bool ViewerDefault { get; init; }

    /// <summary>Acts on the devices with their stored login: the matrix warns.</summary>
    public bool FullDeviceControl { get; init; }

    /// <summary>Icon of the group header (the plugin's icon).</summary>
    public string? IconKey { get; init; }
}

/// <summary>The permissions OADM itself defines (not plugins). Keys are stored in roles: never rename one.</summary>
public static class HostPermissions
{
    public const string DevicesGroup = "Devices";
    public const string TasksGroup = "Tasks";
    public const string ComplianceGroup = "Compliance";
    public const string EventRulesGroup = "Event rules";
    public const string LogsGroup = "Logs";

    public const string DevicesAddRemove = HostPermissionKeys.DevicesAddRemove;
    public const string DevicesRefresh = HostPermissionKeys.DevicesRefresh;
    public const string DevicesLogin = HostPermissionKeys.DevicesLogin;
    public const string DevicesWebInterface = HostPermissionKeys.DevicesWebInterface;
    public const string DevicesLiveView = HostPermissionKeys.DevicesLiveView;
    public const string DevicesOpticsControl = HostPermissionKeys.DevicesOpticsControl;
    public const string DevicesExport = HostPermissionKeys.DevicesExport;
    public const string TagsAssign = HostPermissionKeys.TagsAssign;
    public const string TagsManage = HostPermissionKeys.TagsManage;
    public const string TasksCancel = HostPermissionKeys.TasksCancel;
    public const string TasksDelete = HostPermissionKeys.TasksDelete;
    public const string TasksDeleteAll = HostPermissionKeys.TasksDeleteAll;
    public const string ComplianceView = HostPermissionKeys.ComplianceView;
    public const string ComplianceManage = HostPermissionKeys.ComplianceManage;
    public const string EventsView = HostPermissionKeys.EventsView;
    public const string EventsManage = HostPermissionKeys.EventsManage;
    public const string EventsClearLog = HostPermissionKeys.EventsClearLog;
    public const string LogsServer = HostPermissionKeys.LogsServer;
    public const string SitesView = HostPermissionKeys.SitesView;

    /// <summary>The host rows of the matrix, in order.</summary>
    public static IReadOnlyList<PermissionEntry> All { get; } =
    [
        Host(DevicesAddRemove, "Add and remove devices", "Find devices, add them and remove them from OADM.", DevicesGroup, 0, true, false),
        Host(DevicesRefresh, "Refresh devices", "Read the current state of the devices again.", DevicesGroup, 0, true, false),
        Host(DevicesLogin, "Log in to devices", "Log in with another user name and password, and set the first password of new devices.", DevicesGroup, 0, true, false, fullControl: true),
        Host(DevicesWebInterface, "Open the web interface", "Open a device's web interface in the browser.", DevicesGroup, 0, true, false),
        Host(DevicesLiveView, "Live view", "Click a camera in the device list to see its video.", DevicesGroup, 0, true, true),
        Host(DevicesOpticsControl, "Optics Control", "Zoom and focus cameras in the live view, and set the RTSP port OADM uses for a device.", DevicesGroup, 0, true, false),
        Host(DevicesExport, "Export the device list", "Save the device list as a CSV file.", DevicesGroup, 0, true, false),
        Host(TagsAssign, "Tag devices", "Give devices tags and create new tags.", DevicesGroup, 0, true, false),
        Host(TagsManage, "Rename and delete tags", "Rename, recolor and delete tags on every device.", DevicesGroup, 0, false, false),
        Host(SitesView, "See sites", "See the Sites page: the networks OADM reaches through a site node.", DevicesGroup, 0, true, true),
        Host(TasksCancel, "Cancel tasks", "Cancel running and waiting tasks of every user.", TasksGroup, 1, true, false),
        Host(TasksDelete, "Delete tasks", "Delete single tasks from the task list.", TasksGroup, 1, true, false),
        Host(TasksDeleteAll, "Delete all tasks", "Delete the whole task list.", TasksGroup, 1, false, false),
        Host(ComplianceView, "See compliance", "See the Compliance page and check devices again.", ComplianceGroup, 1, true, true),
        Host(ComplianceManage, "Change compliance checks", "Create, change, turn on or off and delete compliance checks.", ComplianceGroup, 1, false, false),
        Host(EventsView, "See event rules", "See the event rules and the event log.", EventRulesGroup, 2, true, false),
        Host(EventsManage, "Change event rules", "Create, change, turn on or off and delete event rules. Rules act on devices on their own.", EventRulesGroup, 2, false, false, fullControl: true),
        Host(EventsClearLog, "Delete the event log", "Delete every entry of the event log.", EventRulesGroup, 2, false, false),
        Host(LogsServer, "See the server log", "Read the server's log on the Logs page. It shows device addresses and user names.", LogsGroup, 3, false, false),
    ];

    private static PermissionEntry Host(string key, string name, string description, string group, int order, bool operatorDefault, bool viewerDefault, bool fullControl = false) =>
        new(key, name, description, group, order, null)
        {
            OperatorDefault = operatorDefault,
            ViewerDefault = viewerDefault,
            FullDeviceControl = fullControl,
        };
}

/// <summary>
/// Every permission OADM knows: the host's (<see cref="HostPermissions"/>), one per task plugin or its declared ones, the
/// automatic "show" of every plugin page and what core plugins declare. Built from all loaded plugins, turned on or off,
/// so a role keeps the rights of a plugin that is off. Lookups are O(1).
/// </summary>
public sealed class PermissionCatalog
{
    /// <summary>Group order of the first plugin group (after the host groups).</summary>
    private const int PluginGroupOrder = 10;

    private readonly Dictionary<string, PermissionEntry> _byKey;

    private PermissionCatalog(IReadOnlyList<PermissionEntry> entries)
    {
        Entries = entries;
        _byKey = entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    /// <summary>The matrix rows, in order: host groups, then tasks, then plugin groups in rail order.</summary>
    public IReadOnlyList<PermissionEntry> Entries { get; }

    /// <summary>A catalog of the host permissions only (tests, a server without plugins).</summary>
    public static PermissionCatalog HostOnly { get; } = new(HostPermissions.All);

    public bool Contains(string key) => _byKey.ContainsKey(key);

    public PermissionEntry? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The catalog of the host permissions and every plugin in <paramref name="registry"/>.</summary>
    public static PermissionCatalog Build(PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var entries = new List<PermissionEntry>(HostPermissions.All);
        var seen = new HashSet<string>(entries.Select(e => e.Key), StringComparer.Ordinal);
        void Add(PermissionEntry entry)
        {
            if (seen.Add(entry.Key))
            {
                entries.Add(entry);
            }
        }

        // Tasks: in the Tasks group after the host rows, by name; a task with declared permissions lists them in order.
        foreach (var task in registry.AllTaskPlugins.Where(t => t.Plugin.ShowInMenus).OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var entry in TaskEntries(task))
            {
                Add(entry);
            }
        }

        // Core plugins: one group each, in rail order (group, then name).
        foreach (var core in registry.AllCorePlugins
                     .OrderBy(c => SafeGroup(c.Plugin))
                     .ThenBy(c => SafeName(c.Plugin), StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var entry in CoreEntries(core))
            {
                Add(entry);
            }
        }

        return new PermissionCatalog(entries);
    }

    /// <summary>The permission keys of a task plugin: "task:&lt;id&gt;" or its declared "task:&lt;id&gt;:&lt;key&gt;".</summary>
    public static IReadOnlyList<string> TaskKeys(ITaskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var declared = DeclaredOf(plugin);
        return declared.Count == 0 ? [PermissionKeys.Task(plugin.Id)] : [.. declared.Select(p => PermissionKeys.Task(plugin.Id, p.Key))];
    }

    /// <summary>
    /// The permission keys one run of a task plugin needs: "task:&lt;id&gt;" for a plugin without declared permissions, else the
    /// declared ones <see cref="ITaskPlugin.RequiredPermissions"/> names for the payload. No usable answer (null, empty,
    /// only unknown keys, an exception) means every declared permission (fail closed).
    /// </summary>
    public static IReadOnlyList<string> RunKeys(ITaskPlugin plugin, string? payloadJson)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var declared = DeclaredOf(plugin);
        if (declared.Count == 0)
        {
            return [PermissionKeys.Task(plugin.Id)];
        }

        IReadOnlyList<string>? named;
        try
        {
            named = plugin.RequiredPermissions(payloadJson);
        }
#pragma warning disable CA1031 // A plugin that cannot answer needs every declared permission.
        catch (Exception)
#pragma warning restore CA1031
        {
            named = null;
        }

        var keys = (named ?? []).Where(k => declared.Any(p => p.Key == k)).Distinct(StringComparer.Ordinal).ToList();
        return keys.Count == 0
            ? [.. declared.Select(p => PermissionKeys.Task(plugin.Id, p.Key))]
            : [.. keys.Select(k => PermissionKeys.Task(plugin.Id, k))];
    }

    /// <summary>Declared permissions of a task plugin with valid, distinct keys (exceptions: none).</summary>
    public static IReadOnlyList<PermissionDefinition> DeclaredOf(ITaskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        try
        {
            return Valid(plugin.Permissions);
        }
#pragma warning disable CA1031 // A broken plugin declares nothing; its task keeps its one permission.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    /// <summary>Declared permissions of a core plugin with valid, distinct keys; "show" and "manage" are host-made.</summary>
    public static IReadOnlyList<PermissionDefinition> DeclaredOf(ICorePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        try
        {
            return [.. Valid(plugin.Permissions).Where(p => p.Key != PermissionKeys.Show)];
        }
#pragma warning disable CA1031 // A broken plugin declares nothing; it falls back to show / manage.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private static IEnumerable<PermissionEntry> TaskEntries(RegisteredTaskPlugin task)
    {
        var declared = DeclaredOf(task.Plugin);
        if (declared.Count == 0)
        {
            yield return new PermissionEntry(PermissionKeys.Task(task.Id), task.DisplayName, $"Run the task {task.DisplayName} on devices.", HostPermissions.TasksGroup, 1, task.Origin.PackageId)
            {
                OperatorDefault = true,
                IconKey = task.Plugin.IconKey,
            };
            yield break;
        }

        foreach (var p in declared)
        {
            yield return new PermissionEntry(PermissionKeys.Task(task.Id, p.Key), p.Name, p.Description, HostPermissions.TasksGroup, 1, task.Origin.PackageId)
            {
                OperatorDefault = p.OperatorDefault,
                ViewerDefault = p.ViewerDefault,
                FullDeviceControl = p.FullDeviceControl,
                IconKey = task.Plugin.IconKey,
            };
        }
    }

    private static IEnumerable<PermissionEntry> CoreEntries(RegisteredCorePlugin core)
    {
        var plugin = core.Plugin;
        var name = SafeName(plugin);
        var group = SafeGroup(plugin);
        var order = PluginGroupOrder + (int)group;
        PermissionEntry Entry(string key, string title, string description) =>
            new(PermissionKeys.Of(core.Id, key), title, description, name, order, core.Origin.PackageId) { IconKey = plugin.IconKey };

        if (SafeHasPage(plugin))
        {
            // Monitoring and reporting pages only read: the built-in Viewer sees them. A plugin may set the defaults of
            // its page by declaring "show" itself (Web View: off for operators).
            var show = DeclaredShowOf(plugin);
            yield return Entry(PermissionKeys.Show, "Show the page", $"See the {name} page and what it shows.") with
            {
                OperatorDefault = show?.OperatorDefault ?? true,
                ViewerDefault = show?.ViewerDefault ?? group is CorePluginGroup.Monitoring or CorePluginGroup.Reporting,
            };
        }

        var declared = DeclaredOf(plugin);
        if (declared.Count == 0)
        {
            // Only a plugin with methods for administrators (its own RequiredRole) gets the automatic "manage".
            if (AnswersRequiredRole(plugin))
            {
                yield return Entry(PermissionKeys.Manage, "Change settings", $"Everything only administrators could do in {name} before roles existed.");
            }

            yield break;
        }

        foreach (var p in declared)
        {
            yield return Entry(p.Key, p.Name, p.Description) with
            {
                OperatorDefault = p.OperatorDefault,
                ViewerDefault = p.ViewerDefault,
                FullDeviceControl = p.FullDeviceControl,
            };
        }
    }

    /// <summary>True when the plugin implements <see cref="ICorePlugin.RequiredRole"/> itself (the default says Operator for every method).</summary>
    public static bool AnswersRequiredRole(ICorePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var map = plugin.GetType().GetInterfaceMap(typeof(ICorePlugin));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(ICorePlugin.RequiredRole));
        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(ICorePlugin);
    }

    /// <summary>A declared "show" of a core plugin (only its defaults are used), or null.</summary>
    private static PermissionDefinition? DeclaredShowOf(ICorePlugin plugin)
    {
        try
        {
            return plugin.Permissions?.FirstOrDefault(p => p?.Key == PermissionKeys.Show);
        }
#pragma warning disable CA1031 // A broken plugin keeps the usual defaults.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static List<PermissionDefinition> Valid(IReadOnlyList<PermissionDefinition>? declared)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return [.. (declared ?? []).Where(p => p is not null && PermissionKeys.IsValidKey(p.Key) && keys.Add(p.Key) && !string.IsNullOrWhiteSpace(p.Name))];
    }

    private static string SafeName(ICorePlugin plugin)
    {
        try
        {
            return string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.Id : plugin.DisplayName;
        }
#pragma warning disable CA1031 // A broken plugin is listed by its id.
        catch (Exception)
#pragma warning restore CA1031
        {
            return plugin.Id;
        }
    }

    private static CorePluginGroup SafeGroup(ICorePlugin plugin)
    {
        try
        {
            return plugin.Group;
        }
#pragma warning disable CA1031 // A broken plugin goes to Extensions, like in the rail.
        catch (Exception)
#pragma warning restore CA1031
        {
            return CorePluginGroup.Extensions;
        }
    }

    private static bool SafeHasPage(ICorePlugin plugin)
    {
        try
        {
            return plugin.HasPage;
        }
#pragma warning disable CA1031 // Like the rail: a broken plugin has its page.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }
}
