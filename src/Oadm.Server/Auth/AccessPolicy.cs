namespace Oadm.Server.Auth;

/// <summary>Who may call a gRPC method.</summary>
public enum Access
{
    /// <summary>Without a token: AuthService Status, Login and CreateFirstAdmin.</summary>
    Open,

    /// <summary>Any logged-in user (Operator or Admin).</summary>
    Operator,

    /// <summary>Administrators only.</summary>
    Admin,
}

/// <summary>
/// The role table of the gRPC API (CLAUDE.md "Production hardening", Roles). Everything not listed needs a login
/// (Operator); core plugin methods are checked again per method (<c>ICorePlugin.RequiredRole</c>).
/// </summary>
public static class AccessPolicy
{
    private static readonly Dictionary<string, Access> Methods = new(StringComparer.Ordinal)
    {
        ["/oadm.v1.AuthService/Status"] = Access.Open,
        ["/oadm.v1.AuthService/Login"] = Access.Open,
        ["/oadm.v1.AuthService/CreateFirstAdmin"] = Access.Open,

        ["/oadm.v1.SettingsService/Set"] = Access.Admin,
        ["/oadm.v1.SettingsService/AddCredential"] = Access.Admin,
        ["/oadm.v1.SettingsService/RemoveCredential"] = Access.Admin,
        ["/oadm.v1.SettingsService/RevealCredential"] = Access.Admin,
        ["/oadm.v1.TaskService/DeleteAll"] = Access.Admin,
        ["/oadm.v1.PluginService/SetPackageEnabled"] = Access.Admin,

        // Like SetCredentials: operators log in to devices; saving to the credential list is checked in the call.
        ["/oadm.v1.DeviceService/LogIn"] = Access.Operator,
        ["/oadm.v1.DeviceService/GetCredentialUserName"] = Access.Operator,
        ["/oadm.v1.DeviceService/SetFirstPassword"] = Access.Operator,
        ["/oadm.v1.DeviceService/GetPassphrasePolicies"] = Access.Operator,

        // Tags: operators tag devices and create tags; renaming, recoloring and deleting a tag definition is Admin only.
        ["/oadm.v1.TagService/Update"] = Access.Admin,
        ["/oadm.v1.TagService/Delete"] = Access.Admin,
    };

    /// <summary>Services that are Admin only as a whole.</summary>
    private static readonly string[] AdminServices = ["/oadm.v1.UserService/", "/oadm.v1.AuditService/"];

    /// <summary>The methods with an explicit entry (tests check them against the contracts).</summary>
    public static IReadOnlyCollection<string> ListedMethods => Methods.Keys;

    /// <param name="method">gRPC method path, "/oadm.v1.SettingsService/Set".</param>
    public static Access For(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (Methods.TryGetValue(method, out var access))
        {
            return access;
        }

        return AdminServices.Any(s => method.StartsWith(s, StringComparison.Ordinal)) ? Access.Admin : Access.Operator;
    }
}
