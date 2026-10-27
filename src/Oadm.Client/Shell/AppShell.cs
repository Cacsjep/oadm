using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Shell;

/// <summary>
/// Which window the app shows: the terms of use on the first start of this computer (Accept or Quit), the start splash while the client loads (server connection, a remembered login, the first
/// device list), then the login window (real server, nobody remembered) or the main window; the login window again after
/// Log out or when the server ends the session. Fake mode has no login (user "admin", Administrator).
/// </summary>
public sealed partial class AppShell(
    IOadmApi api,
    UserSession session,
    IClientSettingsStore settings,
    AppOptions options,
    Func<LoginViewModel> loginFactory,
    MainWindowViewModel main,
    ILogger<AppShell> logger,
    LiveView.GpuDecodingService? gpu = null,
    Updates.UpdateNotifier? updates = null)
{
    public const string SessionEndedMessage = "Your session has ended. Log in again.";

    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        gpu?.SetEnabled(settings.Current.GpuDecoding); // the GPU decoding test runs in the background, in its own process
        session.LogoutRequested += (_, e) => _ = LogoutAsync(e.Message);
        api.SessionEnded += (_, _) => Dispatcher.UIThread.Post(() => session.RequestLogout(SessionEndedMessage));
        if (TermsViewModel.IsAccepted(settings.Current))
        {
            ShowSplash(desktop);
            return;
        }

        // The terms first: nothing connects before they are accepted; Quit (or the X) ends the client.
        var terms = new Oadm.Theme.Shell.TermsWindow();
        terms.Attach(new TermsViewModel(settings));
        terms.Completed += (_, accepted) =>
        {
            if (accepted)
            {
                ShowSplash(desktop); // the new main window first, so closing the terms window does not end the app
                terms.Close();
            }
            else
            {
                desktop.Shutdown();
            }
        };
        desktop.MainWindow = terms;
        terms.Show();
    }

    private void ShowSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashViewModel();
        var window = new SplashWindow { DataContext = splash };
        desktop.MainWindow = window;
        window.Show();
        _ = StartupAsync(window, splash);
    }

    /// <summary>
    /// The real loading behind the splash: connect and resume a remembered login (real server), then start the streams and
    /// wait for the first device list. The splash stays at least <see cref="SplashViewModel.MinimumDuration"/> (the logo is
    /// complete), then fades out and the login or main window opens.
    /// </summary>
    private async Task StartupAsync(SplashWindow window, SplashViewModel splash)
    {
        var minimum = Task.Delay(SplashViewModel.MinimumDuration);
        LoginViewModel? login = null;
        try
        {
            if (options.UseFake)
            {
                // Fake mode has no login: "admin" (the composition root signs in with the whole role; a second sign-in
                // with less would change the permissions twice and make the pages reload while the splash animates).
                if (!session.IsLoggedIn)
                {
                    session.SignIn(api is FakeOadmApi fake ? fake.CurrentUser() : new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin }, api.ServerAddress);
                }
            }
            else
            {
                splash.Step("Connecting to " + api.ServerAddress, 25);
                login = loginFactory();
                var loggedIn = false;
                void OnLoggedIn(object? sender, EventArgs e) => loggedIn = true;
                login.LoggedIn += OnLoggedIn;
                await login.InitializeAsync().ConfigureAwait(true); // resumes a remembered session
                login.LoggedIn -= OnLoggedIn;
                if (loggedIn)
                {
                    login = null;
                }
            }

            if (login is null)
            {
                splash.Step("Loading devices", 60);
                await LoadDevicesAsync().ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // The splash must never keep the client from starting: the next window shows the problem.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStartupFailed(logger, ex.Message);
        }

        await minimum.ConfigureAwait(true);
        splash.Step("Ready", 100);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);

        // The next window opens behind the splash (still showing "Ready", on top) and draws its first frame; only then the
        // splash fades out over it. Before, the splash faded first and its empty window stood there while the main window
        // was built (about a second).
        window.Topmost = true;
        Window next = login is not null ? ShowLogin(null, login) : ShowMain(started: true);
        await FirstFrameAsync(next).ConfigureAwait(true);
        await window.FadeOutAsync().ConfigureAwait(true);
        window.Close();
        next.Activate();
    }

    /// <summary>Waits until <paramref name="window"/> drew its first frame (two frames after Show), at most 3 s.</summary>
    private static async Task FirstFrameAsync(TopLevel window)
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => drawn.TrySetResult()));
        await Task.WhenAny(drawn.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
    }

    /// <summary>Starts the streams and waits for the first device list (at most <see cref="SplashViewModel.DevicesTimeout"/>).</summary>
    private async Task LoadDevicesAsync()
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? sender, EventArgs e) => loaded.TrySetResult();
        main.Connection.Connected += OnConnected;
        try
        {
            main.Start();
            await Task.WhenAny(loaded.Task, Task.Delay(SplashViewModel.DevicesTimeout)).ConfigureAwait(true);
        }
        finally
        {
            main.Connection.Connected -= OnConnected;
        }
    }

    private LoginWindow ShowLogin(string? notice, LoginViewModel? initialized = null)
    {
        LoginViewModel vm = initialized ?? loginFactory();
        vm.Notice = notice;
        var window = new LoginWindow { DataContext = vm };
        vm.LoggedIn += async (_, _) =>
        {
            // The login window stays in front until the main window drew its first frame.
            window.Topmost = true;
            MainWindow next = ShowMain();
            await FirstFrameAsync(next).ConfigureAwait(true);
            window.Close();
            next.Activate();
        };
        if (_desktop is not null)
        {
            _desktop.MainWindow = window;
        }

        window.Show();
        return window;
    }

    private MainWindow ShowMain(bool started = false)
    {
        var window = new MainWindow { DataContext = main };
        if (_desktop is not null)
        {
            _desktop.MainWindow = window;
        }

        window.Show();
        if (!started)
        {
            main.Start();
        }

        // Once per start, over the main window: a newer OADM release (2 s at most, silent without an answer).
        if (updates is not null)
        {
            _ = updates.ShowIfNewerAsync(CancellationToken.None);
        }

        return window;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Start: loading behind the splash failed: {Reason}")]
    private static partial void LogStartupFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Logout on the server failed: {Reason}")]
    private static partial void LogLogoutFailed(ILogger logger, string reason);
}
