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
    /// PluginService.Invoke, Watch and Download, the task plugin of TaskService.Run and Query.
    /// </summary>
    InCall,

    /// <summary>The Administrator role only (never grantable).</summary>
    Admin,
}

/// <summary>
/// The rule of one gRPC method. <see cref="AllDevices"/> marks methods only users with all devices may call (tag rename and
/// delete, Delete all of tasks, Clear of the event log, the server log, server-wide switches), whatever the role allows:
/// the interceptor refuses them for users with a device group (PERMISSION_DENIED "Only users with all devices can do this.").
/// </summary>
public readonly record struct AccessRule(Access Access, string? Permission = null)
{
    public static AccessRule Open => new(Access.Open);

    public static AccessRule SignedIn => new(Access.SignedIn);

    public static AccessRule InCall => new(Access.InCall);

    public static AccessRule Admin => new(Access.Admin);

    public static AccessRule Needs(string permission) => new(Access.Permission, permission);

    /// <summary>Only users with all devices (administrators always have them).</summary>
    public bool AllDevices { get; init; }

    /// <summary>This rule, for users with all devices only.</summary>
    public AccessRule ForAllDevices() => this with { AllDevices = true };
}

/// <summary>
/// The access table of the gRPC API (docs/spec/security.md "Users, roles and sessions"). Every method of the contracts is listed (a test checks
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
        ["/oadm.v1.DeviceGroupService/List"] = AccessRule.Admin,
        ["/oadm.v1.DeviceGroupService/Get"] = AccessRule.Admin,
        ["/oadm.v1.DeviceGroupService/Save"] = AccessRule.Admin,
        ["/oadm.v1.DeviceGroupService/Delete"] = AccessRule.Admin,
        ["/oadm.v1.DeviceGroupService/Preview"] = AccessRule.Admin,
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
        ["/oadm.v1.WaitingDeviceService/Add"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.WaitingDeviceService/List"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.WaitingDeviceService/Watch"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.WaitingDeviceService/Remove"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.WaitingDeviceService/TryNow"] = AccessRule.Needs(HostPermissions.DevicesAddRemove),
        ["/oadm.v1.TagService/List"] = AccessRule.SignedIn,
        ["/oadm.v1.TagService/Watch"] = AccessRule.SignedIn,
        ["/oadm.v1.TagService/Create"] = AccessRule.Needs(HostPermissions.TagsAssign),
        ["/oadm.v1.TagService/SetDeviceTags"] = AccessRule.Needs(HostPermissions.TagsAssign),
        ["/oadm.v1.TagService/Update"] = AccessRule.Needs(HostPermissions.TagsManage).ForAllDevices(),
        ["/oadm.v1.TagService/Delete"] = AccessRule.Needs(HostPermissions.TagsManage).ForAllDevices(),

        // Tasks: every role sees the task list; running and the dialog's reads depend on the task plugin.
        ["/oadm.v1.TaskService/ListTaskPlugins"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/List"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/Watch"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/GetLog"] = AccessRule.SignedIn,
        ["/oadm.v1.TaskService/Run"] = AccessRule.InCall,
        ["/oadm.v1.TaskService/Query"] = AccessRule.InCall,
        ["/oadm.v1.TaskService/Cancel"] = AccessRule.Needs(HostPermissions.TasksCancel),
        ["/oadm.v1.TaskService/Delete"] = AccessRule.Needs(HostPermissions.TasksDelete),
        ["/oadm.v1.TaskService/DeleteAll"] = AccessRule.Needs(HostPermissions.TasksDeleteAll).ForAllDevices(),

        // Uploads feed task dialogs (firmware, applications, configuration files); size and quota limit them.
        ["/oadm.v1.FileService/Upload"] = AccessRule.SignedIn,
        ["/oadm.v1.FileService/Delete"] = AccessRule.SignedIn,

        // Compliance.
        ["/oadm.v1.ComplianceService/GetCatalog"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/ListChecks"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/WatchChecks"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/GetCheck"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/WatchResults"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/CheckNow"] = AccessRule.Needs(HostPermissions.ComplianceView),
        ["/oadm.v1.ComplianceService/SaveCheck"] = AccessRule.Needs(HostPermissions.ComplianceManage),
        ["/oadm.v1.ComplianceService/DeleteCheck"] = AccessRule.Needs(HostPermissions.ComplianceManage),
        ["/oadm.v1.ComplianceService/SetCheckEnabled"] = AccessRule.Needs(HostPermissions.ComplianceManage),
        ["/oadm.v1.ComplianceService/ListChoices"] = AccessRule.Needs(HostPermissions.ComplianceManage),
        ["/oadm.v1.ComplianceService/SetEnabled"] = AccessRule.Needs(HostPermissions.ComplianceManage).ForAllDevices(), // server-wide switch

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
        ["/oadm.v1.EventService/ClearLog"] = AccessRule.Needs(HostPermissions.EventsClearLog).ForAllDevices(),
        ["/oadm.v1.ServerLogService/List"] = AccessRule.Needs(HostPermissions.LogsServer).ForAllDevices(), // its text names devices

        // Plugins: pages per plugin (in the call); turning packages on or off is Administrator only.
        ["/oadm.v1.PluginService/ListCorePlugins"] = AccessRule.SignedIn,
        ["/oadm.v1.PluginService/ListPackages"] = AccessRule.SignedIn,
        ["/oadm.v1.PluginService/Invoke"] = AccessRule.InCall,
        ["/oadm.v1.PluginService/Watch"] = AccessRule.InCall,
        ["/oadm.v1.PluginService/Download"] = AccessRule.InCall,
        ["/oadm.v1.PluginService/SetPackageEnabled"] = AccessRule.Admin,
        ["/oadm.v1.PluginService/SetPackageLicense"] = AccessRule.Admin,
        ["/oadm.v1.PluginService/RemovePackageLicense"] = AccessRule.Admin,

        // Server settings and the credential list are Administrator only; every role reads the settings (About, read-only page).
        ["/oadm.v1.SettingsService/Get"] = AccessRule.SignedIn,
        ["/oadm.v1.SettingsService/Set"] = AccessRule.Admin.ForAllDevices(),
        ["/oadm.v1.SettingsService/ListCredentials"] = AccessRule.Admin.ForAllDevices(), // never for users with a group
        ["/oadm.v1.SettingsService/AddCredential"] = AccessRule.Admin.ForAllDevices(),
        ["/oadm.v1.SettingsService/RemoveCredential"] = AccessRule.Admin.ForAllDevices(),
        ["/oadm.v1.SettingsService/RevealCredential"] = AccessRule.Admin.ForAllDevices(),
        ["/oadm.v1.LicenseService/GetLicense"] = AccessRule.SignedIn,
        ["/oadm.v1.LicenseService/ImportLicense"] = AccessRule.Admin,
        ["/oadm.v1.LicenseService/RemoveLicense"] = AccessRule.Admin,
        ["/oadm.v1.SupportService/CreateReport"] = AccessRule.Admin,

        ["/oadm.v1.BackupService/GetState"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/SetSchedule"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/Create"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/Download"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/Delete"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/Inspect"] = AccessRule.Admin,
        ["/oadm.v1.BackupService/Restore"] = AccessRule.Admin,
        // Sites: the page needs "See sites"; everything that changes a site is Administrator only, never grantable.
        ["/oadm.v1.SiteService/ListSites"] = AccessRule.Needs(HostPermissions.SitesView),
        ["/oadm.v1.SiteService/WatchSites"] = AccessRule.Needs(HostPermissions.SitesView),
        ["/oadm.v1.SiteService/SetAccept"] = AccessRule.Admin,
        ["/oadm.v1.SiteService/BindSite"] = AccessRule.Admin,
        ["/oadm.v1.SiteService/RenameSite"] = AccessRule.Admin,
        ["/oadm.v1.SiteService/ReplaceSite"] = AccessRule.Admin,
        ["/oadm.v1.SiteService/RemoveSite"] = AccessRule.Admin,
    };

    /// <summary>The listed methods (a test checks them against the contracts, both ways).</summary>
    public static IReadOnlyCollection<string> ListedMethods => Methods.Keys;

    /// <param name="method">gRPC method path, "/oadm.v1.SettingsService/Set".</param>
    public static AccessRule For(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Methods.TryGetValue(method, out var rule) ? rule : AccessRule.Admin;
    }

    /// <summary>
    /// True when <paramref name="caller"/> may make a call with <paramref name="rule"/>: the role (InCall: only the login)
    /// and, for <see cref="AccessRule.AllDevices"/>, all devices.
    /// </summary>
    public static bool Allows(AccessRule rule, AuthenticatedCaller caller) =>
        AllowsRole(rule, caller) && (!rule.AllDevices || caller.HasAllDevices);

    /// <summary>The role part of <see cref="Allows"/>.</summary>
    public static bool AllowsRole(AccessRule rule, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return rule.Access switch
        {
            Access.Open or Access.SignedIn or Access.InCall => true,
            Access.Permission => caller.Has(rule.Permission!),
            _ => caller.IsAdmin,
        };
    }

    /// <summary>The text of PERMISSION_DENIED for <paramref name="caller"/>: what the role lacks, else that the method needs all devices.</summary>
    public static string DeniedMessage(AccessRule rule, PermissionCatalog catalog, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return AllowsRole(rule, caller) ? Core.DeviceGroups.DeviceGroupTexts.AllDevicesOnly : DeniedMessage(rule, catalog);
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
            : entry.Group is HostPermissions.DevicesGroup or HostPermissions.TasksGroup or HostPermissions.EventRulesGroup or HostPermissions.LogsGroup ? entry.Name
            : entry.Group + ": " + entry.Name;
        return $"Your role does not allow: {name}.";
    }
}
