using Oadm.Core.Auth;

namespace Oadm.Server.Auth;

/// <summary>What a gRPC method needs.</summary>
public enum Access
{
    /// <summary>Without a token: AuthService Status, Login and CreateFirstAdmin.</summary>
    Open,

    /// <summary>Any logged-in user, whatever the role.</summary>
    SignedIn,

    /// <summary>The permission <see cref="AccessRule.Permission"/>.</summary>
    Permission,

    /// <summary>
    /// A login here; the call checks the permission itself because it depends on the request: the plugin of
    /// PluginService.Invoke and Watch, the task plugin of TaskService.Run and Query.
    /// </summary>
    InCall,

    /// <summary>The Administrator role only (never grantable).</summary>
    Admin,
}

/// <summary>The rule of one gRPC method.</summary>
public readonly record struct AccessRule(Access Access, string? Permission = null)
{
    public static AccessRule Open => new(Access.Open);

    public static AccessRule SignedIn => new(Access.SignedIn);

    public static AccessRule InCall => new(Access.InCall);

    public static AccessRule Admin => new(Access.Admin);

    public static AccessRule Needs(string permission) => new(Access.Permission, permission);
}

/// <summary>
/// The access table of the gRPC API (docs/rbac-spec-draft.md). Every method of the contracts is listed (a test checks
/// it); a method that is not listed is Administrator only, so a new call is never open by mistake.
/// </summary>
public static class AccessPolicy
{
    /// <summary>Web View's permission: the relay acts with the stored device login.</summary>
    public const string WebViewOpen = "oadm.web-view:open";

    private static readonly Dictionary<string, AccessRule> Methods = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.AuthService/Status"] = AccessRule.Open,
        ["/oadm.v1.AuthService/Login"] = AccessRule.Open,
        ["/oadm.v1.AuthService/CreateFirstAdmin"] = AccessRule.Open,
        ["/oadm.v1.AuthService/Logout"] = AccessRule.SignedIn,
        ["/oadm.v1.AuthService/Me"] = AccessRule.SignedIn,
        ["/oadm.v1.AuthService/WatchSession"] = AccessRule.SignedIn,

        ["/oadm.v1.UserService/List"] = AccessRule.Admin,
        ["/oadm.v1.UserService/Add"] = AccessRule.Admin,
        ["/oadm.v1.UserService/Update"] = AccessRule.Admin,
        ["/oadm.v1.UserService/Delete"] = AccessRule.Admin,
        ["/oadm.v1.AuditService/List"] = AccessRule.Admin,
        ["/oadm.v1.RoleService/GetMatrix"] = AccessRule.Admin,
        ["/oadm.v1.RoleService/SaveRoles"] = AccessRule.Admin,
        ["/oadm.v1.RoleService/DeleteRole"] = AccessRule.Admin,
        ["/oadm.v1.RoleService/Mine"] = AccessRule.SignedIn,

        // Devices: every role sees the list; the rest per permission.
        ["/oadm.v1.DeviceService/List"] = AccessRule.SignedIn,
        ["/oadm.v1.DeviceService/Watch"] = AccessRule.SignedIn,
        ["/oadm.v1.DeviceService/Remove"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DeviceService/Refresh"] = AccessRule.Needs(HostPermissions.DevicesRefresh),
        ["/oadm.v1.DeviceService/SetCredentials"] = AccessRule.Needs(HostPermissions.DevicesLogin),
        ["/oadm.v1.DeviceService/LogIn"] = AccessRule.Needs(HostPermissions.DevicesLogin),
        ["/oadm.v1.DeviceService/GetCredentialUserName"] = AccessRule.Needs(HostPermissions.DevicesLogin),
        ["/oadm.v1.DeviceService/SetFirstPassword"] = AccessRule.Needs(HostPermissions.DevicesLogin),
        ["/oadm.v1.DeviceService/GetPassphrasePolicies"] = AccessRule.Needs(HostPermissions.DevicesLogin),
        ["/oadm.v1.DeviceService/GetWebUiUrl"] = AccessRule.Needs(HostPermissions.DevicesWebInterface),
        ["/oadm.v1.DeviceWebService/Open"] = AccessRule.Needs(WebViewOpen),
        ["/oadm.v1.DeviceWebService/Exchange"] = AccessRule.Needs(WebViewOpen),
        ["/oadm.v1.LiveViewService/Watch"] = AccessRule.Needs(HostPermissions.DevicesLiveView),
        ["/oadm.v1.LiveViewService/ListSources"] = AccessRule.Needs(HostPermissions.DevicesLiveView),
        ["/oadm.v1.LiveViewService/GetOptics"] = AccessRule.Needs(HostPermissions.DevicesLiveView),
        ["/oadm.v1.LiveViewService/SetOptics"] = AccessRule.Needs(HostPermissions.DevicesOpticsControl),
        ["/oadm.v1.LiveViewService/GetRtspPort"] = AccessRule.SignedIn,
        ["/oadm.v1.LiveViewService/GetPtz"] = AccessRule.Needs(HostPermissions.DevicesLiveView),
        ["/oadm.v1.LiveViewService/MovePtz"] = AccessRule.Needs(HostPermissions.DevicesOpticsControl),
        ["/oadm.v1.LiveViewService/SetRtspPort"] = AccessRule.Needs(HostPermissions.DevicesOpticsControl),

        // Adding devices.
        ["/oadm.v1.DiscoveryService/StartZeroConf"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DiscoveryService/StartRangeScan"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DiscoveryService/ProbeAddress"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DiscoveryService/WatchDiscovered"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DiscoveryService/StopScan"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.DiscoveryService/Stop"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.AddDevicesService/Prepare"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.AddDevicesService/Commit"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.AddDevicesService/RetryAuth"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),

        // Tags.
        ["/oadm.v1.TagService/List"] = AccessRule.SignedIn,
        ["/oadm.v1.TagService/Watch"] = AccessRule.SignedIn,
        ["/oadm.v1.TagService/Create"] = AccessRule.Needs(HostPermissions.TagsAssign),
        ["/oadm.v1.TagService/SetDeviceTags"] = AccessRule.Needs(HostPermissions.TagsAssign),
        ["/oadm.v1.TagService/Update"] = AccessRule.Needs(HostPermissions.TagsManage),
        ["/oadm.v1.TagService/Delete"] = AccessRule.Needs(HostPermissions.TagsManage),

        // Tasks: every role sees the task list; running and the dialog's reads depend on the task plugin.
        ["/oadm.v1.TaskService/ListTaskPlugins"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/List"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/Watch"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/GetLog"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/Run"] = AccessRule.InCall,
        ["/oadm.v1.TaskService/Query"] = AccessRule.InCall,
        ["/oadm.v1.TaskService/Cancel"] = AccessRule.Needs(HostPermissions.TasksCancel),
        ["/oadm.v1.TaskService/Delete"] = AccessRule.Needs(HostPermissions.TasksDelete),
        ["/oadm.v1.TaskService/DeleteAll"] = AccessRule.Needs(HostPermissions.TasksDeleteAll),

        // Uploads feed task dialogs (firmware, applications, configuration files); size and quota limit them.
        ["/oadm.v1.FileService/Upload"] = AccessRule.SignedIn,
        ["/oadm.v1.FileService/Delete"] = AccessRule.SignedIn,

        // Event rules.
        ["/oadm.v1.EventService/GetCatalog"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/ListRules"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/WatchRules"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/GetRule"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/ListLog"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/WatchLog"] = AccessRule.Needs(HostPermissions.EventsView),
        ["/oadm.v1.EventService/SaveRule"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/DeleteRule"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/SetRuleEnabled"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/DuplicateRule"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/ListActionChoices"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/TestAction"] = AccessRule.Needs(HostPermissions.EventsManage),
        ["/oadm.v1.EventService/ClearLog"] = AccessRule.Needs(HostPermissions.EventsClearLog),

        // Plugins: pages per plugin (in the call); turning packages on or off is Administrator only.
        ["/oadm.v1.PluginService/ListCorePlugins"] = AccessRule.SignedIn,
        ["/oadm.v1.PluginService/ListPackages"] = AccessRule.SignedIn,
        ["/oadm.v1.PluginService/Invoke"] = AccessRule.InCall,
        ["/oadm.v1.PluginService/Watch"] = AccessRule.InCall,
        ["/oadm.v1.PluginService/SetPackageEnabled"] = AccessRule.Admin,

        // Server settings and the credential list are Administrator only; every role reads the settings (About, read-only page).
        ["/oadm.v1.SettingsService/Get"] = AccessRule.SignedIn,
        ["/oadm.v1.SettingsService/Set"] = AccessRule.Admin,
        ["/oadm.v1.SettingsService/ListCredentials"] = AccessRule.Admin,
        ["/oadm.v1.SettingsService/AddCredential"] = AccessRule.Admin,
        ["/oadm.v1.SettingsService/RemoveCredential"] = AccessRule.Admin,
        ["/oadm.v1.SettingsService/RevealCredential"] = AccessRule.Admin,
    };

    /// <summary>The listed methods (a test checks them against the contracts, both ways).</summary>
    public static IReadOnlyCollection<string> ListedMethods => Methods.Keys;

    /// <param name="method">gRPC method path, "/oadm.v1.SettingsService/Set".</param>
    public static AccessRule For(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Methods.TryGetValue(method, out var rule) ? rule : AccessRule.Admin;
    }

    /// <summary>True when <paramref name="caller"/> may make a call with <paramref name="rule"/> (InCall: only the login).</summary>
    public static bool Allows(AccessRule rule, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return rule.Access switch
        {
            Access.Open or Access.SignedIn or Access.InCall => true,
            Access.Permission => caller.Has(rule.Permission!),
            _ => caller.IsAdmin,
        };
    }

    /// <summary>
    /// The text of PERMISSION_DENIED: what the role lacks, by the name the matrix shows ("Your role does not allow: Delete
    /// tasks.").
    /// </summary>
    public static string DeniedMessage(AccessRule rule, PermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return rule.Access == Access.Permission ? DeniedMessage(rule.Permission!, catalog) : AuthInterceptor.AdminOnlyMessage;
    }

    /// <inheritdoc cref="DeniedMessage(AccessRule, PermissionCatalog)"/>
    public static string DeniedMessage(string permission, PermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entry = catalog.Find(permission);
        var name = entry is null ? permission
            : entry.Group is HostPermissions.DevicesGroup or HostPermissions.TasksGroup or HostPermissions.EventRulesGroup ? entry.Name
            : entry.Group + ": " + entry.Name;
        return $"Your role does not allow: {name}.";
    }
}
