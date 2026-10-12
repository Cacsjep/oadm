namespace Oadm.Sdk.Plugins;

/// <summary>
/// A right an administrator gives or takes per role on the Users page (the permission matrix). A plugin declares its
/// own through <see cref="ICorePlugin.Permissions"/> or <see cref="ITaskPlugin.Permissions"/>; the host stores them as
/// "&lt;plugin id&gt;:&lt;key&gt;" (core plugins) and "task:&lt;task plugin id&gt;:&lt;key&gt;" (task plugins), so keys only have to be
/// unique within the plugin.
/// </summary>
/// <param name="Key">Letters and digits, starting with a letter, at most 32 characters ("send", "rollout").</param>
/// <param name="Name">What the role may do, short, for the matrix row ("Send VAPIX commands").</param>
/// <param name="Description">One sentence that explains it, shown in the row's info icon.</param>
public sealed record PermissionDefinition(string Key, string Name, string Description)
{
    /// <summary>Whether the built-in Operator role gets it when the permission first appears. Default true.</summary>
    public bool OperatorDefault { get; init; } = true;

    /// <summary>Whether the built-in Viewer role (sees, changes nothing) gets it when it first appears. Default false.</summary>
    public bool ViewerDefault { get; init; }

    /// <summary>
    /// The permission gives full control of the devices (it acts with their stored login, e.g. Web View, user accounts on
    /// the devices): the matrix marks it with a warning icon.
    /// </summary>
    public bool FullDeviceControl { get; init; }
}

/// <summary>
/// The keys of the permissions OADM itself defines (the host's rows of the matrix). Stored in roles: never rename one.
/// Plugins and the client check them like any other key.
/// </summary>
public static class HostPermissionKeys
{
    public const string DevicesAddRemove = "devices.addRemove";
    public const string DevicesRefresh = "devices.refresh";
    public const string DevicesLogin = "devices.login";
    public const string DevicesWebInterface = "devices.webInterface";
    public const string DevicesLiveView = "devices.liveView";
    public const string DevicesOpticsControl = "devices.opticsControl";
    public const string DevicesExport = "devices.export";
    public const string TagsAssign = "tags.assign";
    public const string TagsManage = "tags.manage";
    public const string TasksCancel = "tasks.cancel";
    public const string TasksDelete = "tasks.delete";
    public const string TasksDeleteAll = "tasks.deleteAll";
    public const string EventsView = "events.view";
    public const string EventsManage = "events.manage";
    public const string EventsClearLog = "events.clearLog";
}

/// <summary>Keys of permissions: the format the host stores and checks.</summary>
public static class PermissionKeys
{
    /// <summary>The automatic permission of every plugin page: see the page, read its state, watch its events.</summary>
    public const string Show = "show";

    /// <summary>
    /// The automatic permission of a core plugin that declares none: the methods that need <see cref="UserRole.Admin"/>.
    /// </summary>
    public const string Manage = "manage";

    /// <summary>Longest key a plugin may declare.</summary>
    public const int MaxKeyLength = 32;

    /// <summary>"oadm.dhcp-server:manage".</summary>
    public static string Of(string pluginId, string key) => pluginId + ":" + key;

    /// <summary>"task:oadm.restart": the one permission of a task plugin without declared permissions.</summary>
    public static string Task(string taskPluginId) => "task:" + taskPluginId;

    /// <summary>"task:oadm.users:remove": a declared permission of a task plugin.</summary>
    public static string Task(string taskPluginId, string key) => "task:" + taskPluginId + ":" + key;

    /// <summary>Letters and digits, starting with a letter, 1..<see cref="MaxKeyLength"/> characters.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && char.IsAsciiLetter(key[0]) && key.All(char.IsAsciiLetterOrDigit);
}
