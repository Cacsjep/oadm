using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;

namespace Oadm.Client.Shell;

/// <summary>Why the user has to log in (again).</summary>
public sealed class LogoutRequestedEventArgs(string? message) : EventArgs
{
    /// <summary>Shown in the login window; null after a plain Log out.</summary>
    public string? Message { get; } = message;
}

/// <summary>
/// The logged-in user of this client: shown at the bottom of the navigation rail, decides what the pages show: the
/// Administrator-only parts (Users page, Audit tab, credential list) and every action by its permission
/// (<see cref="Has"/>). The server checks every call again; this is only the view. Role and permissions follow the
/// server live (AuthService.WatchSession through <see cref="Update"/>).
/// </summary>
public sealed partial class UserSession : ObservableObject
{
    private bool _logoutPending;
    private HashSet<string> _permissions = new(StringComparer.Ordinal);

    /// <summary>The server is older than roles: it sends no permissions.</summary>
    private bool _legacy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn), nameof(RoleText))]
    public partial string? UserName { get; private set; }

    /// <summary>The Administrator role: every permission and the Administrator-only pages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleText))]
    public partial bool IsAdmin { get; private set; }

    /// <summary>The role's name ("Operator"); empty for an older server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleText))]
    public partial string RoleName { get; private set; } = "";

    /// <summary>The page the role starts on after a login (a rail key; empty = Devices).</summary>
    public string StartPage { get; private set; } = "";

    /// <summary>The server of the session ("https://localhost:5080").</summary>
    [ObservableProperty]
    public partial string ServerAddress { get; private set; } = "";

    public bool IsLoggedIn => UserName is not null;

    public string RoleText => !IsLoggedIn ? "" : RoleName.Length > 0 ? RoleName : IsAdmin ? "Administrator" : "Operator";

    /// <summary>The user asked to log out, or the server ended the session. The app shows the login window.</summary>
    public event EventHandler<LogoutRequestedEventArgs>? LogoutRequested;

    /// <summary>Raised after the role or its permissions changed (pages show or hide actions, the rail reloads pages).</summary>
    public event EventHandler? PermissionsChanged;

    /// <summary>
    /// Whether the role allows <paramref name="permission"/> ("devices.addRemove", "task:oadm.users:remove"). An older server
    /// sends no permissions: then the Administrator has everything and others everything but the Administrator-only parts,
    /// like before roles.
    /// </summary>
    public bool Has(string permission) => IsAdmin || _permissions.Contains(permission) || (_legacy && IsLoggedIn);

    public void SignIn(UserInfo user, string serverAddress)
    {
        ArgumentNullException.ThrowIfNull(user);
        UserName = user.UserName;
        ServerAddress = serverAddress;
        _logoutPending = false;
        Apply(user);
    }

    /// <summary>The server reported the user again (role or permissions may have changed).</summary>
    public void Update(UserInfo user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (IsLoggedIn && string.Equals(user.UserName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            Apply(user);
        }
    }

    public void SignOut()
    {
        UserName = null;
        IsAdmin = false;
        RoleName = "";
        StartPage = "";
        _permissions = new(StringComparer.Ordinal);
        _legacy = false;
    }

    private void Apply(UserInfo user)
    {
        bool admin = user.AllPermissions || user.Role == UserRole.Admin;
        var permissions = new HashSet<string>(user.Permissions, StringComparer.Ordinal);

        // A server before roles sends neither a role id nor permissions.
        bool legacy = string.IsNullOrEmpty(user.RoleId);
        bool changed = admin != IsAdmin || legacy != _legacy || !permissions.SetEquals(_permissions) || user.RoleName != RoleName;
        _permissions = permissions;
        _legacy = legacy;
        RoleName = user.RoleName;
        StartPage = user.StartPage;
        IsAdmin = admin;
        if (changed)
        {
            PermissionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raises <see cref="LogoutRequested"/> once per session (a burst of failing calls asks once).</summary>
    public void RequestLogout(string? message)
    {
        if (_logoutPending || !IsLoggedIn)
        {
            return;
        }

        _logoutPending = true;
        LogoutRequested?.Invoke(this, new LogoutRequestedEventArgs(message));
    }
}
