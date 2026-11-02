using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Help;
using Oadm.Client.MyAxis;
using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// The MyAxis sign-in in the client (docs/spec/myaxis.md): the page rule, the cookie that is sent, the state from the
/// server, the sign-in window's flow with a fake cookie source and a fake server, the title bar button and its panel.
/// No real browser (headless). OADM_SCREENSHOT_DIR writes client-myaxis-*-dark.png / -light.png.
/// </summary>
public sealed class MyAxisTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static string Appearance =>
        string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

    private static MyAxisStateReply SignedIn() => new() { SignedIn = true, SignedInAt = Timestamp.FromDateTimeOffset(Since) };

    private static RpcException Refused() => new(new Status(StatusCode.FailedPrecondition, MyAxisTexts.NotAccepted));

    private static Cookie Sid(string value = "abc123", string domain = ".axis.com") => new(MyAxisPages.SessionCookie, value, "/", domain);

    // ---- The page rule

    [Theory]
    [InlineData("https://www.axis.com/ftp/pub_soft/")]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/")]
    [InlineData("https://www.axis.com/en-us/products")]
    [InlineData("https://WWW.AXIS.COM/")]
    public void Pages_on_www_axis_com_after_the_login_read_the_cookies(string page) =>
        Assert.True(MyAxisPages.ReadsCookies(new Uri(page)));

    [Theory]
    [InlineData("https://www.axis.com/my-axis/login?axis_destination=https%3A%2F%2Fwww.axis.com%2Fftp%2Fpub_soft%2F")]
    [InlineData("https://www.axis.com/en-us/my-axis/login")]
    [InlineData("https://www.axis.com/de-de/my-axis/login/verify")]
    [InlineData("https://www.axis.com/login")]
    [InlineData("https://eu.login.connect.axis.com/login")]
    [InlineData("https://eu.login.connect.axis.com/ftp/pub_soft/")]
    [InlineData("https://axis.com/ftp/pub_soft/")]
    [InlineData("http://www.axis.com/ftp/pub_soft/")]
    [InlineData("https://www.axis.com:8443/ftp/pub_soft/")]
    [InlineData("https://www.axis.com.example.com/ftp/pub_soft/")]
    [InlineData("about:blank")]
    public void Login_pages_and_other_sites_never_read_the_cookies(string page) =>
        Assert.False(MyAxisPages.ReadsCookies(new Uri(page)));

    [Fact]
    public void The_window_starts_on_the_login_page_back_to_the_download_index()
    {
        Assert.False(MyAxisPages.ReadsCookies(null));
        Assert.Equal("https://www.axis.com/my-axis/login?axis_destination=https%3A%2F%2Fwww.axis.com%2Fftp%2Fpub_soft%2F",
            MyAxisPages.StartAddress.AbsoluteUri);
        Assert.False(MyAxisPages.ReadsCookies(MyAxisPages.StartAddress));
        Assert.True(MyAxisPages.ReadsCookies(MyAxisPages.DoneAddress));
    }

    // ---- The cookie that is sent

    [Fact]
    public void Only_the_session_cookie_of_axis_com_is_sent_as_one_pair()
    {
        Cookie[] cookies =
        [
            new("token", "t0k3n", "/", ".axis.com"),
            new("axis_auth", "1", "/", "www.axis.com"),
            Sid("s3ss10n", ".axis.com"),
            new("other", "x", "/", "www.axis.com"),
        ];

        Assert.Equal("axis_connect_session_sid=s3ss10n", MyAxisPages.SessionOf(cookies));
        Assert.Equal("axis_connect_session_sid=a", MyAxisPages.SessionOf([Sid("a", "www.axis.com")]));
        Assert.Equal("axis_connect_session_sid=a", MyAxisPages.SessionOf([Sid("a", "axis.com")]));
    }

    [Fact]
    public void No_session_cookie_or_one_the_server_would_refuse_is_none()
    {
        Assert.Null(MyAxisPages.SessionOf(null));
        Assert.Null(MyAxisPages.SessionOf([]));
        Assert.Null(MyAxisPages.SessionOf([new Cookie("token", "t0k3n", "/", ".axis.com")])); // only the session cookie counts
        Assert.Null(MyAxisPages.SessionOf([Sid("a", ".connect.axis.com")])); // another host's cookie
        Assert.Null(MyAxisPages.SessionOf([Sid("a", "example.com")]));
        Assert.Null(MyAxisPages.SessionOf([Sid(new string('a', MyAxisPages.MaxSessionLength))])); // longer than the server takes
        var expired = Sid();
        expired.Expires = DateTime.Now.AddYears(-1);
        Assert.Null(MyAxisPages.SessionOf([expired]));
        Assert.Equal("axis_connect_session_sid=ok", MyAxisPages.SessionOf([Sid("a", "example.com"), Sid("ok")])); // the first that fits
    }

    // ---- The state

    [Fact]
    public async Task The_state_comes_from_the_server_on_every_connect()
    {
        using var ui = new UiThread();
        await ui.RunAsync(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            api.FakeMyAxis = SignedIn();
            var devices = new DeviceStore();
            using var connection = new ServerConnection(api, devices, new TaskStore(devices), ui, NullLogger<ServerConnection>.Instance);
            var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance, connection, ui: ui);
            var changes = 0;
            var onUi = true;
            account.Changed += (_, _) =>
            {
                changes++;
                onUi &= ui.CheckAccess();
            };

            connection.Start();
            await Eventually.TrueAsync(() => account.IsSignedIn, "signed in after the connect");

            Assert.True(account.IsAvailable);
            Assert.Equal(1, changes);
            Assert.True(onUi);
            Assert.Equal(Since, account.SignedInAt);
            Assert.Equal("Signed in to MyAxis", account.ButtonTip);
            Assert.False(account.HasWarning);
            Assert.Equal("Signed in to MyAxis since " + Since.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), account.SinceText);
            connection.Stop();
        });
    }

    [Fact]
    public async Task Signed_out_expired_and_an_older_server()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply());
        var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance);
        var changes = 0;
        account.Changed += (_, _) => changes++;

        await account.RefreshAsync(CancellationToken.None);
        Assert.True(account.IsAvailable);
        Assert.False(account.IsSignedIn);
        Assert.Equal("Sign in to MyAxis", account.ButtonTip);
        Assert.False(account.HasWarning);
        Assert.Equal(0, changes); // nothing changed

        api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply { Expired = true });
        await account.RefreshAsync(CancellationToken.None);
        Assert.True(account.IsExpired);
        Assert.Equal("Sign in to MyAxis again", account.ButtonTip);
        Assert.True(account.HasWarning);
        Assert.Equal(1, changes);

        await account.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, changes); // the same answer again raises nothing

        api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Throws(new RpcException(new Status(StatusCode.Unimplemented, "")));
        await account.RefreshAsync(CancellationToken.None);
        Assert.False(account.IsAvailable); // the button hides
        Assert.False(account.HasWarning);
    }

    [Fact]
    public async Task Sign_out_and_logout_end_the_state()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(SignedIn());
        api.SignOutMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply());
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "anna", Role = UserRole.Admin, AllPermissions = true }, "server");
        var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance, session: session);

        await account.OpenCommand.ExecuteAsync(null);
        Assert.True(account.IsSignedIn);
        Assert.True(account.IsFlyoutOpen); // signed in: the panel, no window

        await account.SignOutCommand.ExecuteAsync(null);
        await api.Received(1).SignOutMyAxisAsync(Arg.Any<CancellationToken>());
        Assert.False(account.IsSignedIn);
        Assert.False(account.IsFlyoutOpen);

        await account.RefreshAsync(CancellationToken.None);
        Assert.True(account.IsSignedIn);
        session.SignOut();
        Assert.False(account.IsSignedIn); // the next user reads their own
    }

    [Fact]
    public async Task Signed_out_the_button_asks_for_the_window()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply { Expired = true });
        var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance);
        var asked = 0;
        account.SignInRequested += (_, _) => asked++;

        await account.OpenCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.False(account.IsFlyoutOpen);
    }

    [Fact]
    public void Task_dialogs_get_the_sign_in()
    {
        var api = Substitute.For<IOadmApi>();
        var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance);

        Assert.Same(account, new TaskDialogContext(api, "oadm.firmware", myAxis: account).MyAxis);
        Assert.Null(new TaskDialogContext(api, "oadm.firmware").MyAxis);
    }

    // ---- The sign-in window's flow

    /// <summary>A fake clock that counts its timers: the test advances only once the pause started.</summary>
    private sealed class CountingTime : FakeTimeProvider
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    private sealed class FakeCookies(Func<int, IReadOnlyList<Cookie>?> read) : ICookieSource
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public List<Uri> Addresses { get; } = [];

        public Task<IReadOnlyList<Cookie>?> ReadAsync(Uri address, CancellationToken ct)
        {
            lock (Addresses)
            {
                Addresses.Add(address);
            }

            return Task.FromResult(read(Interlocked.Increment(ref _reads)));
        }
    }

    private static (MyAxisSignInViewModel SignIn, List<MyAxisStateReply> Accepted) NewSignIn(IOadmApi api, TimeProvider time, EmbeddedBrowserState? browser = null)
    {
        var accepted = new List<MyAxisStateReply>();
        var signIn = new MyAxisSignInViewModel(api, accepted.Add, () => browser ?? EmbeddedBrowserState.Available, time, NullLogger.Instance);
        signIn.Start();
        return (signIn, accepted);
    }

    [Fact]
    public async Task A_wrong_code_or_password_shows_the_page_of_Axis_not_the_connection_error()
    {
        var (signIn, _) = NewSignIn(Substitute.For<IOadmApi>(), new FakeTimeProvider());
        var cookies = new FakeCookies(_ => []);
        var otpPage = new Uri("https://auth.axis.com/authn/authentication/email-wcode/enter-otp");

        await signIn.PageLoadedAsync(MyAxisPages.StartAddress, cookies, CancellationToken.None);
        await signIn.PageLoadedAsync(otpPage, cookies, CancellationToken.None, succeeded: false); // Axis answers 4xx

        Assert.Null(signIn.LoadErrorText);
        Assert.True(signIn.IsBrowserVisible);
        Assert.Equal(0, cookies.Reads);
    }

    [Fact]
    public async Task The_session_cookie_after_the_login_is_sent_and_the_window_closes()
    {
        var api = Substitute.For<IOadmApi>();
        api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SignedIn());
        var (signIn, accepted) = NewSignIn(api, new FakeTimeProvider());
        var cookies = new FakeCookies(_ => [new Cookie("token", "t", "/", ".axis.com"), Sid("s3ss10n")]);
        var closed = 0;
        signIn.CloseRequested += (_, _) => closed++;

        await signIn.PageLoadedAsync(MyAxisPages.StartAddress, cookies, CancellationToken.None); // the login page: nothing
        Assert.Equal(0, cookies.Reads);

        await signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None);

        await api.Received(1).SignInMyAxisAsync("axis_connect_session_sid=s3ss10n", Arg.Any<CancellationToken>());
        Assert.Equal([MyAxisPages.CookieAddress], cookies.Addresses);
        Assert.True(signIn.IsAccepted);
        Assert.Single(accepted);
        Assert.Equal(1, closed);
        Assert.False(signIn.IsChecking);
        Assert.Null(signIn.ErrorText);

        await signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None); // accepted once
        Assert.Equal(1, cookies.Reads);
    }

    [Fact]
    public async Task A_refusal_is_tried_again_six_times_half_a_second_apart_then_the_login_page_again()
    {
        var api = Substitute.For<IOadmApi>();
        api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(Refused());
        var time = new CountingTime();
        var (signIn, accepted) = NewSignIn(api, time);
        var cookies = new FakeCookies(_ => [Sid()]);
        var navigated = new List<Uri>();
        signIn.NavigateRequested += (_, page) => navigated.Add(page);

        var run = signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None);
        Assert.True(signIn.IsChecking); // covered from the first moment
        Assert.Equal("Checking the sign-in", signIn.OverlayText);
        Assert.False(signIn.IsBrowserVisible);
        for (var attempt = 1; attempt < MyAxisSignInViewModel.Attempts; attempt++)
        {
            // The window's load timer is the first timer, the pauses follow.
            await Eventually.TrueAsync(() => time.Timers == attempt + 1, $"pause {attempt}");
            Assert.Equal(attempt, cookies.Reads);
            time.Advance(MyAxisSignInViewModel.RetryDelay - TimeSpan.FromMilliseconds(1));
            Assert.Equal(attempt, cookies.Reads); // not before the pause is over
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await run;
        Assert.Equal(MyAxisSignInViewModel.Attempts, cookies.Reads);
        await api.Received(MyAxisSignInViewModel.Attempts).SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("MyAxis did not accept the sign-in. Try again.", signIn.ErrorText);
        Assert.True(signIn.HasError);
        Assert.Equal([MyAxisPages.StartAddress], navigated);
        Assert.False(signIn.IsChecking);
        Assert.False(signIn.IsAccepted);
        Assert.Empty(accepted);
        Assert.Equal("Loading MyAxis", signIn.OverlayText); // the login page again, covered until it is there

        await signIn.PageLoadedAsync(MyAxisPages.StartAddress, cookies, CancellationToken.None);
        Assert.Null(signIn.OverlayText);
        Assert.True(signIn.IsBrowserVisible);
        Assert.True(signIn.HasError); // the refusal stays above the login page
    }

    [Fact]
    public async Task A_session_that_completes_a_moment_later_is_accepted_on_a_retry()
    {
        var api = Substitute.For<IOadmApi>();
        var calls = 0;
        api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => ++calls < 3 ? throw Refused() : Task.FromResult(SignedIn()));
        var time = new CountingTime();
        var (signIn, accepted) = NewSignIn(api, time);
        // The first read has no session cookie yet.
        var cookies = new FakeCookies(n => n == 1 ? [new Cookie("token", "t", "/", ".axis.com")] : [Sid()]);

        var run = signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None);
        for (var reads = 1; reads <= 3; reads++)
        {
            await Eventually.TrueAsync(() => time.Timers == reads + 1, $"pause {reads}");
            time.Advance(MyAxisSignInViewModel.RetryDelay);
        }

        await run;
        Assert.Equal(4, cookies.Reads);
        Assert.Equal(3, calls);
        Assert.True(signIn.IsAccepted);
        Assert.Single(accepted);
        Assert.Null(signIn.ErrorText);
    }

    [Fact]
    public async Task Another_server_error_says_so_at_once()
    {
        var api = Substitute.For<IOadmApi>();
        api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new RpcException(new Status(StatusCode.Unavailable, "axis.com cannot be reached.")));
        var (signIn, _) = NewSignIn(api, new FakeTimeProvider());
        var cookies = new FakeCookies(_ => [Sid()]);

        await signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None);

        Assert.Equal(1, cookies.Reads);
        Assert.Contains("axis.com cannot be reached.", signIn.ErrorText, StringComparison.Ordinal);
        Assert.False(signIn.IsAccepted);
    }

    [Fact]
    public async Task No_browser_or_no_way_to_the_cookies_is_not_available()
    {
        var api = Substitute.For<IOadmApi>();
        var (noBrowser, _) = NewSignIn(api, new FakeTimeProvider(), new EmbeddedBrowserState(false, EmbeddedBrowserTexts.LinuxMissing));
        Assert.True(noBrowser.IsNotAvailable);
        Assert.False(noBrowser.ShowsBrowser);
        Assert.Equal("Sign-in is not available here. " + EmbeddedBrowserTexts.LinuxMissing, noBrowser.NotAvailableText);

        var (noCookies, _) = NewSignIn(api, new FakeTimeProvider());
        Assert.False(noCookies.IsNotAvailable);
        await noCookies.PageLoadedAsync(MyAxisPages.DoneAddress, new FakeCookies(_ => null), CancellationToken.None);
        Assert.Equal("Sign-in is not available here.", noCookies.NotAvailableText);
        await api.DidNotReceiveWithAnyArgs().SignInMyAxisAsync(default!, default);
    }

    [Fact]
    public async Task Sign_in_opens_the_window_and_is_true_once_accepted()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = Substitute.For<IOadmApi>();
            api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SignedIn());
            var shown = 0;
            var account = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance, browser: () => EmbeddedBrowserState.Available,
                time: new FakeTimeProvider(), showWindow: async (_, signIn) =>
                {
                    shown++;
                    await signIn.PageLoadedAsync(MyAxisPages.DoneAddress, new FakeCookies(_ => [Sid()]), CancellationToken.None);
                });
            var owner = new Window();
            owner.Show();

            Assert.True(await account.SignInAsync(owner));
            Assert.True(account.IsSignedIn);
            Assert.True(await account.SignInAsync(owner)); // already signed in: no window
            Assert.Equal(1, shown);

            var closedEarly = new MyAxisAccount(api, NullLogger<MyAxisAccount>.Instance, browser: () => EmbeddedBrowserState.Available,
                showWindow: (_, _) => Task.CompletedTask);
            Assert.False(await closedEarly.SignInAsync(owner));
            owner.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    // ---- The window and the title bar (headless)

    [Fact]
    public async Task The_login_page_loads_under_the_overlay_and_signing_in_covers_the_page_after_the_login()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var factory = MyAxisSignInWindow.BrowserFactory;
            try
            {
                var browser = new DisposableText();
                MyAxisSignInWindow.BrowserFactory = _ => browser;
                var api = Substitute.For<IOadmApi>();
                var answer = new TaskCompletionSource<MyAxisStateReply>();
                api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
                var (signIn, _) = NewSignIn(api, new FakeTimeProvider());
                var window = new MyAxisSignInWindow { DataContext = signIn };
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Show();
                Render();

                // From the first moment: the ring over the hidden browser, no blank page.
                Assert.Same(browser, window.Browser);
                Assert.Equal("Sign in to MyAxis", window.Title);
                var overlay = window.FindControl<LoadingOverlay>("Overlay")!;
                var host = window.FindControl<Panel>("BrowserHost")!;
                Assert.True(overlay.IsVisible);
                Assert.Equal("Loading MyAxis", overlay.Text);
                Assert.False(host.IsVisible);
                Save(window.CaptureRenderedFrame(), outDir, $"client-myaxis-loading-{Appearance}.png");

                // The login page is there: the user types on it.
                var cookies = new FakeCookies(_ => [Sid()]);
                await signIn.PageLoadedAsync(MyAxisPages.StartAddress, cookies, CancellationToken.None);
                Render();
                Assert.False(overlay.IsVisible);
                Assert.True(host.IsVisible);

                // The next step of the login (another page of the login): loading again, no sign-in yet.
                var otp = new Uri("https://eu.login.connect.axis.com/login/otp");
                signIn.NavigationStarting(otp);
                Render();
                Assert.Equal("Loading MyAxis", overlay.Text);
                await signIn.PageLoadedAsync(otp, cookies, CancellationToken.None);
                Assert.True(signIn.IsBrowserVisible);
                Assert.Equal(0, cookies.Reads);

                // The login heads back to axis.com: covered before the page shows, through the check.
                signIn.NavigationStarting(MyAxisPages.DoneAddress);
                Render();
                Assert.True(overlay.IsVisible);
                Assert.Equal("Signing in", overlay.Text);
                Assert.False(host.IsVisible);
                Save(window.CaptureRenderedFrame(), outDir, $"client-myaxis-signing-in-{Appearance}.png");

                var run = signIn.PageLoadedAsync(MyAxisPages.DoneAddress, cookies, CancellationToken.None);
                Render();
                Assert.False(host.IsVisible); // the download listing never shows
                Assert.Equal("Checking the sign-in", overlay.Text);

                answer.SetResult(SignedIn());
                await run;
                Dispatcher.UIThread.RunJobs();
                Assert.True(closed);
                Assert.True(browser.IsDisposed);
                Assert.Null(window.Browser);
                Assert.True(signIn.HasOverlay); // covered until the window is gone
            }
            finally
            {
                MyAxisSignInWindow.BrowserFactory = factory;
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_page_that_fails_to_load_shows_the_error_with_Try_again()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var factory = MyAxisSignInWindow.BrowserFactory;
            try
            {
                MyAxisSignInWindow.BrowserFactory = _ => new DisposableText();
                var (signIn, _) = NewSignIn(Substitute.For<IOadmApi>(), new FakeTimeProvider());
                var navigated = new List<Uri>();
                signIn.NavigateRequested += (_, page) => navigated.Add(page);
                var window = new MyAxisSignInWindow { DataContext = signIn };
                window.Show();
                Render();

                await signIn.PageLoadedAsync(MyAxisPages.StartAddress, new FakeCookies(_ => []), CancellationToken.None, succeeded: false);
                Render();
                var error = window.FindControl<StackPanel>("LoadError")!;
                Assert.True(error.IsEffectivelyVisible);
                Assert.Equal("axis.com could not be reached. Check the internet connection.", signIn.LoadErrorText);
                Assert.False(window.FindControl<LoadingOverlay>("Overlay")!.IsVisible);
                Assert.False(window.FindControl<Panel>("BrowserHost")!.IsVisible);
                var tryAgain = window.FindControl<Button>("TryAgain")!;
                Assert.Equal("Try again", tryAgain.Content);
                Assert.Contains("primary", tryAgain.Classes);
                Save(window.CaptureRenderedFrame(), outDir, $"client-myaxis-not-reachable-{Appearance}.png");

                tryAgain.Command!.Execute(null);
                Render();
                Assert.Equal([MyAxisPages.StartAddress], navigated);
                Assert.False(error.IsVisible);
                Assert.Equal("Loading MyAxis", signIn.OverlayText);
                window.Close();
            }
            finally
            {
                MyAxisSignInWindow.BrowserFactory = factory;
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_page_that_does_not_load_in_time_shows_the_error()
    {
        var time = new FakeTimeProvider();
        var (signIn, _) = NewSignIn(Substitute.For<IOadmApi>(), time);
        Assert.Equal("Loading MyAxis", signIn.OverlayText);

        time.Advance(MyAxisSignInViewModel.LoadTimeout - TimeSpan.FromSeconds(1));
        Assert.False(signIn.HasLoadError);
        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => signIn.HasLoadError, "the load error");
        Assert.Null(signIn.OverlayText);
        Assert.False(signIn.IsBrowserVisible);

        // A page that finished in time: no error later.
        signIn.TryAgainCommand.Execute(null);
        await signIn.PageLoadedAsync(MyAxisPages.StartAddress, new FakeCookies(_ => []), CancellationToken.None);
        time.Advance(MyAxisSignInViewModel.LoadTimeout * 2);
        Assert.False(signIn.HasLoadError);
        Assert.True(signIn.IsBrowserVisible);
    }

    [Fact]
    public async Task A_page_after_the_login_that_fails_to_show_still_signs_in()
    {
        var api = Substitute.For<IOadmApi>();
        api.SignInMyAxisAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SignedIn());
        var (signIn, _) = NewSignIn(api, new FakeTimeProvider());

        await signIn.PageLoadedAsync(MyAxisPages.DoneAddress, new FakeCookies(_ => [Sid()]), CancellationToken.None, succeeded: false);

        Assert.True(signIn.IsAccepted);
        Assert.False(signIn.HasLoadError);
    }

    [Fact]
    public async Task The_window_without_a_browser_says_sign_in_is_not_available()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var factory = MyAxisSignInWindow.BrowserFactory;
            try
            {
                MyAxisSignInWindow.BrowserFactory = _ => throw new InvalidOperationException("no browser when not available");
                var (signIn, _) = NewSignIn(Substitute.For<IOadmApi>(), new FakeTimeProvider(), new EmbeddedBrowserState(false, EmbeddedBrowserTexts.LinuxMissing));
                var window = new MyAxisSignInWindow { DataContext = signIn };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var chip = window.FindControl<StatusChip>("NotAvailable")!;
                Assert.True(chip.IsEffectivelyVisible);
                Assert.StartsWith("Sign-in is not available here.", chip.Text, StringComparison.Ordinal);
                Assert.False(window.FindControl<Panel>("BrowserHost")!.IsVisible);
                Assert.Null(window.Browser);
                Save(window.CaptureRenderedFrame(), outDir, $"client-myaxis-not-available-{Appearance}.png");
                window.Close();

                // A browser that cannot be created is the same.
                MyAxisSignInWindow.BrowserFactory = _ => throw new PlatformNotSupportedException();
                var (broken, _) = NewSignIn(Substitute.For<IOadmApi>(), new FakeTimeProvider());
                var second = new MyAxisSignInWindow { DataContext = broken };
                second.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Sign-in is not available here.", broken.NotAvailableText);
                second.Close();
            }
            finally
            {
                MyAxisSignInWindow.BrowserFactory = factory;
            }

            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_title_bar_button_sits_left_of_Help_and_shows_each_state()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var f = new DevicesFixture();
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var session = new UserSession();
            session.SignIn(new UserInfo { UserName = "anna", Role = UserRole.Operator, RoleName = "Operator" }, "token");
            f.Api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply());
            var account = new MyAxisAccount(f.Api, NullLogger<MyAxisAccount>.Instance, connection, session,
                browser: () => EmbeddedBrowserState.Available, showWindow: (_, _) => Task.CompletedTask);
            var asked = 0;
            account.SignInRequested += (_, _) => asked++;
            var shell = f.CreateShell(connection, session, myAxis: account);
            var main = new MainWindow { DataContext = shell, Width = 1400, Height = 600 };
            main.Show();
            await account.RefreshAsync(CancellationToken.None);
            Render();

            // Signed out: every user sees it, directly left of Help, icon only like Help.
            var button = main.FindControl<ToolbarButton>("TitleMyAxis")!;
            var host = main.FindControl<Panel>("TitleMyAxisHost")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsIconOnly);
            Assert.Contains("iconOnly", button.Classes);
            Assert.Equal("cloud", button.IconKey);
            Assert.True(Application.Current!.TryGetResource("Icon.cloud", null, out var icon) && icon is Avalonia.Media.Geometry);
            Assert.Equal("Sign in to MyAxis", ToolTip.GetTip(button));
            var row = (StackPanel)host.GetVisualParent()!;
            var order = row.Children.ToList();
            Assert.Equal(order.IndexOf(main.FindControl<ToolbarButton>("TitleHelp")!) - 1, order.IndexOf(host));
            Assert.False(main.FindControl<Avalonia.Controls.Shapes.Ellipse>("TitleMyAxisWarning")!.IsVisible);
            SaveTitleBar(main, outDir, "signed-out");

            await account.OpenCommand.ExecuteAsync(null);
            Assert.Equal(1, asked); // signed out: the sign-in window

            // Expired: sign in again, with the warning dot.
            f.Api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply { Expired = true });
            await account.RefreshAsync(CancellationToken.None);
            Render();
            Assert.Equal("Sign in to MyAxis again", ToolTip.GetTip(button));
            Assert.True(main.FindControl<Avalonia.Controls.Shapes.Ellipse>("TitleMyAxisWarning")!.IsEffectivelyVisible);
            SaveTitleBar(main, outDir, "expired");

            // Signed in: a click opens the panel with the date and Sign out.
            f.Api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Returns(SignedIn());
            await account.OpenCommand.ExecuteAsync(null);
            Render();
            Assert.Equal("Signed in to MyAxis", ToolTip.GetTip(button));
            Assert.False(main.FindControl<Avalonia.Controls.Shapes.Ellipse>("TitleMyAxisWarning")!.IsVisible);
            var popup = main.FindControl<Popup>("TitleMyAxisFlyout")!;
            Assert.True(popup.IsOpen);
            var since = main.FindControl<TextBlock>("TitleMyAxisSince")!;
            Assert.True(since.IsEffectivelyVisible);
            Assert.StartsWith("Signed in to MyAxis since ", since.Text, StringComparison.Ordinal);
            var signOut = main.FindControl<Button>("TitleMyAxisSignOut")!;
            Assert.Equal("Sign out", signOut.Content);
            Assert.Contains("secondary", signOut.Classes);
            SaveTitleBar(main, outDir, "signed-in"); // the panel opens in the window, so the crop shows it

            f.Api.SignOutMyAxisAsync(Arg.Any<CancellationToken>()).Returns(new MyAxisStateReply());
            signOut.Command!.Execute(null);
            await Eventually.TrueAsync(() => !account.IsSignedIn, "signed out");
            Render();
            Assert.False(popup.IsOpen);
            Assert.Equal("Sign in to MyAxis", ToolTip.GetTip(button));

            // An older server: no button.
            f.Api.GetMyAxisAsync(Arg.Any<CancellationToken>()).Throws(new RpcException(new Status(StatusCode.Unimplemented, "")));
            await account.RefreshAsync(CancellationToken.None);
            Render();
            Assert.False(host.IsVisible);

            shell.Stop();
            main.Close();
            Dispatcher.UIThread.RunJobs();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The right end of the title bar (and what opened below it): MyAxis, Help, the user, Log out.</summary>
    private static void SaveTitleBar(MainWindow main, string? outDir, string state)
    {
        var frame = main.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        var width = 560;
        var height = 160;
        var source = new PixelRect(frame.PixelSize.Width - width, 0, width, height);
        using var crop = new RenderTargetBitmap(new PixelSize(width, height), frame.Dpi);
        using (var context = crop.CreateDrawingContext())
        {
            context.DrawImage(frame, source.ToRect(1), new Rect(0, 0, width, height));
        }

        Save(crop, outDir, $"client-myaxis-titlebar-{state}-{Appearance}.png");
    }

    private static void Save(Bitmap? frame, string? outDir, string name)
    {
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }

    private sealed class DisposableText : TextBlock, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
