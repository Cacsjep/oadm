using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.Users;

/// <summary>What the "Users..." task does on every selected device.</summary>
public enum UsersMode
{
    Add = 0,
    Change = 1,
    Remove = 2,
}

/// <summary>Device role, as in the device web UI and ADM. PTZ control is a separate flag.</summary>
public enum UserRole
{
    /// <summary>Listed by the device but member of no access group.</summary>
    None = 0,
    Viewer = 1,
    Operator = 2,
    Administrator = 3,
}

/// <summary>Device passphrase policy (systemready <c>passphrasepolicy</c>, AXIS OS 12).</summary>
public enum PassphrasePolicy
{
    /// <summary>Only the VAPIX base rule: 1-64 printable ASCII characters.</summary>
    None = 0,

    /// <summary>At least 15 characters.</summary>
    Length = 1,

    /// <summary>At least 12 characters with an upper-case letter, a lower-case letter, a digit and a special character.</summary>
    Complex = 2,
}

/// <summary>
/// Payload sent from the dialog to the server. It carries the password, so it is kept in memory only
/// (the task engine never persists payloads) and <see cref="ToString"/> never prints it.
/// </summary>
public sealed class UsersPayload
{
    public UsersMode Mode { get; init; }

    public string UserName { get; init; } = string.Empty;

    /// <summary>New password for Add, or for Change when <see cref="ChangePassword"/> is set. Never logged.</summary>
    public string? Password { get; init; }

    public UserRole Role { get; init; } = UserRole.Viewer;

    public bool Ptz { get; init; }

    /// <summary>Change mode: set a new password.</summary>
    public bool ChangePassword { get; init; }

    /// <summary>Change mode: set <see cref="Role"/> and <see cref="Ptz"/>.</summary>
    public bool ChangeRole { get; init; }

    public override string ToString() =>
        $"{Mode} user '{UserName}', role {UserRoles.Describe(Role, Ptz)}, changePassword={ChangePassword}, changeRole={ChangeRole}";
}

/// <summary>One account on the device.</summary>
public sealed record DeviceUser(string Name, UserRole Role, bool Ptz, bool IsCurrentAccount = false);

/// <summary>Result of the read-only query <c>listUsers</c>.</summary>
public sealed record UsersQueryResult(
    bool Supported,
    string? ApiVersion,
    string? Message,
    string? CurrentAccount,
    PassphrasePolicy Policy,
    IReadOnlyList<DeviceUser> Users);

/// <summary>JSON shape shared by the server and the client part (camelCase, enums as camelCase strings).</summary>
public static class UsersJson
{
    public const string ListUsersMethod = "listUsers";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(UsersPayload payload) => JsonSerializer.Serialize(payload, Options);

    public static string Serialize(UsersQueryResult result) => JsonSerializer.Serialize(result, Options);

    /// <summary>Parses a payload; throws <see cref="ArgumentException"/> with a user-facing message when it is missing or malformed.</summary>
    public static UsersPayload ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The task has no settings (payload missing). Nothing was changed.");
        }

        try
        {
            return JsonSerializer.Deserialize<UsersPayload>(json, Options)
                ?? throw new ArgumentException("The task settings are empty. Nothing was changed.");
        }
        catch (JsonException)
        {
            // The exception text could quote the payload, which may contain the password: do not chain it.
            throw new ArgumentException("The task settings could not be read. Nothing was changed.");
        }
    }

    public static UsersQueryResult? ParseQueryResult(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UsersQueryResult>(json, Options);
}

/// <summary>Mapping between roles and the VAPIX access groups (pwdgrp.cgi <c>sgrp</c>).</summary>
public static class UserRoles
{
    /// <summary>The colon separated <c>sgrp</c> value: Administrator is admin:operator:viewer, Operator is operator:viewer, Viewer is viewer; PTZ appends :ptz.</summary>
    public static string ToSecondaryGroups(UserRole role, bool ptz)
    {
        var groups = role switch
        {
            UserRole.Administrator => "admin:operator:viewer",
            UserRole.Operator => "operator:viewer",
            UserRole.Viewer => "viewer",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "A user needs a role."),
        };
        return ptz ? groups + ":ptz" : groups;
    }

    public static string Name(UserRole role) => role switch
    {
        UserRole.Administrator => "Administrator",
        UserRole.Operator => "Operator",
        UserRole.Viewer => "Viewer",
        _ => "No access",
    };

    /// <summary>"Administrator with PTZ", "Viewer".</summary>
    public static string Describe(UserRole role, bool ptz) => ptz && role != UserRole.None ? Name(role) + " with PTZ" : Name(role);
}
