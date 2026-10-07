namespace Oadm.Plugins.Users;

/// <summary>What the task will do on one device, decided before anything is written.</summary>
public enum PlanKind
{
    /// <summary>Send the write request.</summary>
    Write = 0,

    /// <summary>Nothing to do on this device; <see cref="UserChangePlan.Message"/> explains why.</summary>
    Skip = 1,
}

/// <summary>Outcome of <see cref="UserChangePlanner.Plan"/>. <see cref="IsWarning"/> marks a skip the user should notice.</summary>
public sealed record UserChangePlan(PlanKind Kind, string Message, bool IsWarning = false)
{
    /// <summary>Expected account after the write; null when the account is removed.</summary>
    public DeviceUser? Expected { get; init; }

    public bool SetsPassword { get; init; }

    public bool SetsRole { get; init; }
}

/// <summary>
/// Pure decision logic for one device: validation, existence checks and lock-out protection. Throws
/// <see cref="UserManagementException"/> (Failed, "Nothing was changed") when the change is refused.
/// </summary>
public static class UserChangePlanner
{
    private static readonly Version RootRemovableSince = new(11, 5);

    /// <param name="currentAccount">The account OADM is authenticated as (usergroup.cgi); null when unknown.</param>
    /// <param name="firmwareVersion">AXIS OS version; before 11.5 the account root cannot be removed.</param>
    public static UserChangePlan Plan(
        UsersPayload payload,
        IReadOnlyList<DeviceUser> users,
        string? currentAccount,
        PassphrasePolicy policy,
        string? firmwareVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(users);
        ValidatePayload(payload, policy);

        var name = payload.UserName;
        var existing = users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
        var isCurrent = currentAccount is not null && string.Equals(name, currentAccount, StringComparison.OrdinalIgnoreCase);
        var adminCount = users.Count(u => u.Role == UserRole.Administrator);

        switch (payload.Mode)
        {
            case UsersMode.Add:
                if (existing is not null)
                {
                    return new UserChangePlan(PlanKind.Skip, $"User '{name}' already exists ({UserRoles.Describe(existing.Role, existing.Ptz)}); nothing was changed. Use Change to update it.", IsWarning: true);
                }

                return new UserChangePlan(PlanKind.Write, $"Add user '{name}' as {UserRoles.Describe(payload.Role, payload.Ptz)}")
                {
                    Expected = new DeviceUser(name, payload.Role, payload.Ptz),
                    SetsPassword = true,
                    SetsRole = true,
                };

            case UsersMode.Change:
                if (existing is null)
                {
                    return new UserChangePlan(PlanKind.Skip, $"User '{name}' does not exist on this device; nothing was changed.", IsWarning: true);
                }

                RequireKnownAccount(currentAccount, "change");
                if (isCurrent && payload.ChangePassword)
                {
                    throw new UserManagementException($"'{name}' is the account OADM uses for this device. Changing its password here would lock OADM out; change it with the device credentials instead. Nothing was changed.");
                }

                var demotes = payload.ChangeRole && existing.Role == UserRole.Administrator && payload.Role != UserRole.Administrator;
                if (demotes && isCurrent)
                {
                    throw new UserManagementException($"'{name}' is the account OADM uses for this device and must stay Administrator. Nothing was changed.");
                }

                if (demotes && adminCount <= 1)
                {
                    throw new UserManagementException($"'{name}' is the last administrator on this device and must stay Administrator. Nothing was changed.");
                }

                var roleChanges = payload.ChangeRole && (existing.Role != payload.Role || existing.Ptz != payload.Ptz);
                if (!payload.ChangePassword && !roleChanges)
                {
                    return new UserChangePlan(PlanKind.Skip, $"User '{name}' already is {UserRoles.Describe(existing.Role, existing.Ptz)}; nothing to change.");
                }

                var parts = new List<string>();
                if (payload.ChangePassword)
                {
                    parts.Add("set a new password");
                }

                if (roleChanges)
                {
                    parts.Add($"change role from {UserRoles.Describe(existing.Role, existing.Ptz)} to {UserRoles.Describe(payload.Role, payload.Ptz)}");
                }

                return new UserChangePlan(PlanKind.Write, $"User '{name}': {string.Join(" and ", parts)}")
                {
                    Expected = roleChanges ? new DeviceUser(name, payload.Role, payload.Ptz) : existing,
                    SetsPassword = payload.ChangePassword,
                    SetsRole = roleChanges,
                };

            case UsersMode.Remove:
                if (existing is null)
                {
                    return new UserChangePlan(PlanKind.Skip, $"User '{name}' does not exist on this device; nothing was changed.", IsWarning: true);
                }

                RequireKnownAccount(currentAccount, "remove");
                if (isCurrent)
                {
                    throw new UserManagementException($"'{name}' is the account OADM uses for this device and cannot be removed. Nothing was changed.");
                }

                if (existing.Role == UserRole.Administrator && adminCount <= 1)
                {
                    throw new UserManagementException($"'{name}' is the last administrator on this device and cannot be removed. Nothing was changed.");
                }

                if (string.Equals(name, "root", StringComparison.Ordinal) && IsOlderThan(firmwareVersion, RootRemovableSince))
                {
                    throw new UserManagementException("The account root cannot be removed on AXIS OS older than 11.5. Nothing was changed.");
                }

                return new UserChangePlan(PlanKind.Write, $"Remove user '{name}'");

            default:
                throw new UserManagementException($"Unknown mode {payload.Mode}. Nothing was changed.");
        }
    }

    /// <summary>Input validation shared by the dialog and the server. Throws with a user-facing reason.</summary>
    public static void ValidatePayload(UsersPayload payload, PassphrasePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Fail(CredentialRules.ValidateUserName(payload.UserName));
        switch (payload.Mode)
        {
            case UsersMode.Add:
                Fail(payload.Role == UserRole.None ? "Choose a role." : null);
                Fail(CredentialRules.ValidatePassword(payload.Password, policy));
                break;
            case UsersMode.Change:
                Fail(!payload.ChangePassword && !payload.ChangeRole ? "Choose what to change: password, role or both." : null);
                Fail(payload.ChangeRole && payload.Role == UserRole.None ? "Choose a role." : null);
                Fail(payload.ChangePassword ? CredentialRules.ValidatePassword(payload.Password, policy) : null);
                break;
            case UsersMode.Remove:
                break;
            default:
                Fail($"Unknown mode {payload.Mode}.");
                break;
        }

        static void Fail(string? reason)
        {
            if (reason is not null)
            {
                throw new UserManagementException(reason + " Nothing was changed.");
            }
        }
    }

    private static void RequireKnownAccount(string? currentAccount, string verb)
    {
        if (currentAccount is null)
        {
            throw new UserManagementException($"Could not determine which account OADM uses on this device, so no user is {verb}d to avoid a lock-out. Nothing was changed.");
        }
    }

    /// <summary>True when the version is known and lower; an unknown version is treated as old (safe side).</summary>
    internal static bool IsOlderThan(string? firmwareVersion, Version minimum)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion))
        {
            return true;
        }

        var parts = firmwareVersion.Trim().Split('.');
        return parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)
            || new Version(major, minor) < minimum;
    }
}
