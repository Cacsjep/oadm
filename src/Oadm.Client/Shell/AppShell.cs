using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Shell;

/// <summary>
/// Which window the app shows: the login window first (real server), the main window after a login, the login window
/// again after Log out or when the server ends the session. Fake mode has no login (user "admin", Administrator).
/// </summary>
public sealed partial class AppShell(
    IOadmApi api,
    UserSession session,
    IClientSettingsStore settings,
    AppOptions options,
    Func<LoginViewModel> loginFactory,
    MainWindowViewModel main,
    ILogger<AppShell> logger)
{
    public const string SessionEndedMessage = "Your session has ended. Log in again.";

    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        session.LogoutRequested += (_, e) => _ = LogoutAsync(e.Message);
        api.SessionEnded += (_, _) => Dispatcher.UIThread.Post(() => session.RequestLogout(SessionEndedMessage));
        if (options.UseFake)
        {
            session.SignIn(new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin }, api.ServerAddress);
            ShowMain();
        }
        else
        {
            ShowLogin(null);
        }
    }

    private void ShowLogin(string? notice)
    {
        LoginViewModel vm = loginFactory();
        vm.Notice = notice;
        var window = new LoginWindow { DataContext = vm };
        vm.LoggedIn += (_, _) =>
        {
            ShowMain();
            window.Close();
        };
        if (_desktop is not null)
        {
            _desktop.MainWindow = window;
        }

        window.Show();
    }

    private void ShowMain()
    {
        var window = new MainWindow { DataContext = main };
        if (_desktop is not null)
        {
            _desktop.MainWindow = window;
        }

        window.Show();
        main.Start();
    }

    private async Task LogoutAsync(string? message)
    {
        string server = api.ServerAddress;
        if (message is null)
        {
            try
            {
                await api.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // The session ends on this client in any case.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLogoutFailed(logger, ex.Message);
            }
        }

        try
        {
            if (settings.Current.RememberedLogins.Remove(LoginViewModel.KeyOf(server)))
            {
                settings.Save();
            }
        }
        catch (FormatException)
        {
            // Fake mode ("fake"): nothing remembered.
        }

        LogLoggedOut(logger, session.UserName ?? "", server);
        main.Stop();
        api.AccessToken = null;
        session.SignOut();
        var old = _desktop?.MainWindow;
        ShowLogin(message);
        old?.Close();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{UserName} logged out of {Server}")]
    private static partial void LogLoggedOut(ILogger logger, string userName, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Logout on the server failed: {Reason}")]
    private static partial void LogLogoutFailed(ILogger logger, string reason);
}
