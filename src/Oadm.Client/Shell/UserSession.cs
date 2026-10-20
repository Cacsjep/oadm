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

    /// <summary>
    /// What <see cref="Has"/> answers from: one immutable snapshot, replaced as a whole on the UI thread. Background loops
    /// (ServerConnection's streams) read it while the session changes: they see the old or the new rights, never a set
    /// half written.
    /// </summary>
    private sealed record Rights(bool LoggedIn, bool Admin, bool Legacy, System.Collections.Frozen.FrozenSet<string> Keys)
    {
        public static readonly Rights None = new(false, false, false, System.Collections.Frozen.FrozenSet<string>.Empty);

        public bool Has(string permission) => Admin || Keys.Contains(permission) || (Legacy && LoggedIn);
    }

    private Rights _rights = Rights.None;

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
    public bool Has(string permission) => Volatile.Read(ref _rights).Has(permission);

    /// <summary>Counts the logins of this client: pages that keep state (Sites) start empty for a new one.</summary>
    public int SignInCount { get; private set; }

    public void SignIn(UserInfo user, string serverAddress)
    {
        ArgumentNullException.ThrowIfNull(user);
        SignInCount++;
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
        Volatile.Write(ref _rights, Rights.None);
        UserName = null;
        IsAdmin = false;
        RoleName = "";
    }

    private void Apply(UserInfo user)
    {
        bool admin = user.AllPermissions || user.Role == UserRole.Admin;
        var permissions = System.Collections.Frozen.FrozenSet.ToFrozenSet(user.Permissions, StringComparer.Ordinal);

        // A server before roles sends neither a role id nor permissions.
        bool legacy = string.IsNullOrEmpty(user.RoleId);
        Rights before = _rights;
        bool changed = admin != IsAdmin || legacy != before.Legacy || !permissions.SetEquals(before.Keys) || user.RoleName != RoleName;
        Volatile.Write(ref _rights, new Rights(LoggedIn: true, admin, legacy, permissions));
        RoleName = user.RoleName;
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
