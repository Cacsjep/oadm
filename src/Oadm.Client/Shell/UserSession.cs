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
/// The logged-in user of this client: shown at the bottom of the navigation rail, decides which admin-only parts the
/// pages show (Users card, Audit tab, credential list). The server checks every call again; this is only the view.
/// </summary>
public sealed partial class UserSession : ObservableObject
{
    private bool _logoutPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn), nameof(RoleText))]
    public partial string? UserName { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleText))]
    public partial bool IsAdmin { get; private set; }

    /// <summary>The server of the session ("https://localhost:5080").</summary>
    [ObservableProperty]
    public partial string ServerAddress { get; private set; } = "";

    public bool IsLoggedIn => UserName is not null;

    public string RoleText => !IsLoggedIn ? "" : IsAdmin ? "Administrator" : "Operator";

    /// <summary>The user asked to log out, or the server ended the session. The app shows the login window.</summary>
    public event EventHandler<LogoutRequestedEventArgs>? LogoutRequested;

    public void SignIn(UserInfo user, string serverAddress)
    {
        ArgumentNullException.ThrowIfNull(user);
        UserName = user.UserName;
        IsAdmin = user.Role == UserRole.Admin;
        ServerAddress = serverAddress;
        _logoutPending = false;
    }

    public void SignOut()
    {
        UserName = null;
        IsAdmin = false;
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
