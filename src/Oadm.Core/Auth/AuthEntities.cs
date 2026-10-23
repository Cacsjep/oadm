using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>An OADM user (table Users). The password is stored only as a PBKDF2 hash (<see cref="PasswordHasher"/>).</summary>
public sealed class UserEntity
{
    public Guid Id { get; set; }

    /// <summary>As entered (1..64 characters).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Upper-case invariant user name: unique, so names are unique case-insensitively.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>The user's role (<see cref="RoleEntity"/>).</summary>
    public Guid RoleId { get; set; }

    /// <summary>
    /// The role as the SDK and older clients know it: Admin for the built-in Administrator role, Operator for every other.
    /// </summary>
    public UserRole Role => BuiltInRoles.IsAdministrator(RoleId) ? UserRole.Admin : UserRole.Operator;

    public bool Disabled { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    /// <summary>
    /// The user may reach every device (migration <c>DeviceGroups</c>: true for every user that existed before). False:
    /// the devices of <see cref="DeviceGroupId"/>, or none without a group. Administrators always have all devices.
    /// </summary>
    public bool AllDevices { get; set; }

    /// <summary>The user's device group (table DeviceGroups, delete restricted: deleting a group clears it first).</summary>
    public Guid? DeviceGroupId { get; set; }

    /// <summary>The stored device access.</summary>
    public DeviceScope DeviceAccess => new(AllDevices, DeviceGroupId);

    public override string ToString() => $"User {UserName}";
}

/// <summary>What a role is: one of the three built-in roles, or one an administrator created.</summary>
public enum RoleKind
{
    Custom = 0,

    /// <summary>Everything, including permissions that appear later. Not editable, not deletable.</summary>
    Administrator = 1,

    /// <summary>Editable, "Reset to default" brings back the default set.</summary>
    Operator = 2,

    /// <summary>Sees devices, live view and the monitoring pages. Editable like Operator.</summary>
    Viewer = 3,
}

/// <summary>The fixed ids of the built-in roles (the migration creates them).</summary>
public static class BuiltInRoles
{
    public static readonly Guid AdministratorId = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid OperatorId = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid ViewerId = new("00000000-0000-0000-0000-000000000003");

    public static bool IsAdministrator(Guid roleId) => roleId == AdministratorId;

    /// <summary>The role id of an old two-value role (migration, older clients).</summary>
    public static Guid IdOf(UserRole role) => role == UserRole.Admin ? AdministratorId : OperatorId;
}

/// <summary>
/// A role (table Roles): a name and the permission keys it grants. <see cref="KnownJson"/> holds every key the role has
/// seen, so a permission that appears later (a new plugin) gets its default once: built-in roles by the plugin's default,
/// custom roles nothing until an administrator gives it.
/// </summary>
public sealed class RoleEntity
{
    public Guid Id { get; set; }

    /// <summary>1..64 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Upper-case invariant name: unique, so names are unique case-insensitively.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Optional, at most 200 characters.</summary>
    public string Description { get; set; } = string.Empty;

    public RoleKind Kind { get; set; }

    /// <summary>JSON array of the granted permission keys (unknown keys are kept).</summary>
    public string PermissionsJson { get; set; } = "[]";

    /// <summary>JSON array of every permission key the role has seen.</summary>
    public string KnownJson { get; set; } = "[]";

    /// <summary>
    /// The page the client opens after a login (<see cref="RoleStore.MaxStartPageLength"/>): a rail page key ("compliance",
    /// "plugin:oadm.live-view"); empty = Devices.
    /// </summary>
    public string StartPage { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime ChangedUtc { get; set; }

    public override string ToString() => $"Role {Name} ({Kind})";
}

/// <summary>A login session (table AuthTokens): only the SHA-256 of the token is stored.</summary>
public sealed class AuthTokenEntity
{
    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public string ClientAddress { get; set; } = string.Empty;

    public bool Remember { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Sliding: moved forward on use (written at most once per minute).</summary>
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>One audit log entry (table AuditEntries).</summary>
public sealed class AuditEntryEntity
{
    public long Id { get; set; }

    public DateTime TimeUtc { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string ClientAddress { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}
