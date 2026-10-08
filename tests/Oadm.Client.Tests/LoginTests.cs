using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.Security;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>Login window, first administrator, server certificate trust, Users card and Audit tab.</summary>
public sealed class LoginTests
{
    private const string Fingerprint = "3F:A2:9C:11:5B:0E:77:D4:C2:18:9A:6F:30:E5:B7:21:4C:88:0D:FA:62:93:AB:1E:57:C9:04:D6:2B:7F:E0:35";

    private sealed class Fixture
    {
        public IOadmApi Api { get; } = Substitute.For<IOadmApi>();
        public IServerTrust Trust { get; } = Substitute.For<IServerTrust>();
        public InMemoryClientSettingsStore Settings { get; } = new();
        public UserSession Session { get; } = new();
        public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();
        public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

        public Fixture()
        {
            Api.ServerAddress.Returns("https://localhost:5080");
            Api.GetAuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AuthStatus { ServerName = "srv", Version = "1.0" });
            Dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(ci =>
            {
                Confirmations.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2)));
                return true;
            });
        }

        public LoginViewModel Create(string? server = null) =>
            new(Api, Trust, Settings, Session, Dialogs, new AppOptions { ServerAddress = server }, NullLogger<LoginViewModel>.Instance);
    }

    private static LoginReply Reply(string user, UserRole role = UserRole.Admin) =>
        new() { Token = "tok-" + user, User = new UserInfo { Id = Guid.NewGuid().ToString(), UserName = user, Role = role } };

    [Fact]
    public async Task Login_signs_in_remembers_the_server_and_with_remember_me_the_token()
    {
        var f = new Fixture();
        f.Api.LoginAsync("anna", "Secret-pass-1", true, Arg.Any<CancellationToken>()).Returns(Reply("anna"));
        LoginViewModel vm = f.Create("nvr01:5080");
        bool loggedIn = false;
        vm.LoggedIn += (_, _) => loggedIn = true;
        await vm.InitializeAsync();
        Assert.False(vm.IsFirstAdminMode);
        Assert.False(vm.SubmitCommand.CanExecute(null)); // nothing entered yet, no error shown either
        Assert.Null(vm.ErrorOf(nameof(vm.UserName)));

        vm.UserName = "anna";
        vm.Password = "Secret-pass-1";
        vm.RememberMe = true;
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(loggedIn);
        f.Api.Received().SetServerAddress("nvr01:5080");
        Assert.Equal("anna", f.Session.UserName);
        Assert.True(f.Session.IsAdmin);
        Assert.Equal(["nvr01:5080"], f.Settings.Current.RecentServers);
        Assert.Equal("nvr01:5080", f.Settings.Current.ServerAddress);
        Assert.Equal("tok-anna", f.Settings.Current.RememberedLogins["nvr01:5080"].Token);
        Assert.Equal("", vm.Password); // never kept
    }

    [Fact]
    public async Task A_wrong_password_shows_below_the_password_field()
    {
        var f = new Fixture();
        f.Api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<LoginReply>>(_ => throw new RpcException(new Status(StatusCode.Unauthenticated, "The user name or password is wrong.")));
        LoginViewModel vm = f.Create();
        vm.UserName = "anna";
        vm.Password = "nope";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("The user name or password is wrong.", vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.SubmitCommand.CanExecute(null));
        Assert.Null(f.Session.UserName);
        vm.Password = "again";
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_users_the_form_creates_the_first_administrator_with_the_setup_code()
    {
        var f = new Fixture();
        f.Api.GetAuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AuthStatus { NeedsFirstAdmin = true, SetupCodeRequired = true });
        f.Api.CreateFirstAdminAsync("boss", "Boss-pass-123", "ABCD-EFGH-JKLM", false, Arg.Any<CancellationToken>()).Returns(Reply("boss"));
        LoginViewModel vm = f.Create("server:5080");
        await vm.InitializeAsync();

        Assert.True(vm.IsFirstAdminMode);
        Assert.True(vm.SetupCodeRequired);
        Assert.Equal("Create the first administrator", vm.Title);
        Assert.Equal("Create administrator", vm.SubmitText);

        vm.UserName = "boss";
        vm.Password = "short";
        vm.ConfirmPassword = "other";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("Use at least 10 characters.", vm.ErrorOf(nameof(vm.Password)));
        Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.ConfirmPassword)));
        Assert.Equal("Enter the setup code from the server.", vm.ErrorOf(nameof(vm.SetupCode)));

        vm.Password = "Boss-pass-123";
        vm.ConfirmPassword = "Boss-pass-123";
        vm.SetupCode = "ABCD-EFGH-JKLM";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("boss", f.Session.UserName);
    }

    [Fact]
    public async Task First_connection_asks_to_trust_the_fingerprint_then_logs_in()
    {
        var f = new Fixture();
        int calls = 0;
        f.Api.GetAuthStatusAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls == 1
            ? throw new RpcException(new Status(StatusCode.Unavailable, "The SSL connection could not be established"))
            : Task.FromResult(new AuthStatus()));
        f.Trust.LastCheck.Returns(_ => calls == 1 ? PinCheck.Unknown : PinCheck.Trusted);
        f.Trust.PresentedFingerprint.Returns(Fingerprint);
        f.Api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Reply("anna"));
        LoginViewModel vm = f.Create();
        vm.UserName = "anna";
        vm.Password = "Secret-pass-1";

        await vm.SubmitCommand.ExecuteAsync(null);

        var (title, message, confirm) = Assert.Single(f.Confirmations);
        Assert.Equal("Trust this server?", title);
        Assert.Equal("Trust server", confirm);
        Assert.Contains(LoginViewModel.InTwoLines(Fingerprint), message, StringComparison.Ordinal);
        Assert.Equal(Fingerprint, LoginViewModel.InTwoLines(Fingerprint).Replace("\n", "", StringComparison.Ordinal));
        f.Trust.Received(1).Trust(Fingerprint);
        Assert.Equal("anna", f.Session.UserName);
    }

    [Fact]
    public async Task A_refused_fingerprint_or_a_changed_certificate_stops_the_login()
    {
        var f = new Fixture();
        f.Api.GetAuthStatusAsync(Arg.Any<CancellationToken>())
            .Returns<Task<AuthStatus>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "ssl")));
        f.Trust.PresentedFingerprint.Returns(Fingerprint);
        f.Trust.LastCheck.Returns(PinCheck.Unknown);
        f.Dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        LoginViewModel vm = f.Create();
        vm.UserName = "anna";
        vm.Password = "Secret-pass-1";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.StartsWith("The server certificate was not trusted.", vm.ServerProblem, StringComparison.Ordinal);
        f.Trust.DidNotReceive().Trust(Arg.Any<string>());

        f.Trust.LastCheck.Returns(PinCheck.Changed);
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(LoginViewModel.CertificateChangedMessage, vm.ServerProblem);
        Assert.True(vm.CanForgetServer);
        await f.Api.DidNotReceive().LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        f.Trust.LastCheck.Returns(PinCheck.None);
        await vm.ForgetServerCommand.ExecuteAsync(null);
        f.Trust.Received(1).Forget();
        Assert.Equal("Cannot reach the OADM server at localhost:5080. Check the address and that the server is running.", vm.ServerProblem);
        Assert.False(vm.CanForgetServer);
    }

    [Fact]
    public async Task A_remembered_session_logs_in_without_asking_and_an_expired_one_is_forgotten()
    {
        var f = new Fixture();
        f.Settings.Current.RememberedLogins["localhost:5080"] = new RememberedLogin { UserName = "anna", Token = "remembered" };
        f.Api.GetCurrentUserAsync(Arg.Any<CancellationToken>()).Returns(new UserInfo { UserName = "anna", Role = UserRole.Operator });
        LoginViewModel vm = f.Create();
        bool loggedIn = false;
        vm.LoggedIn += (_, _) => loggedIn = true;
        await vm.InitializeAsync();
        Assert.True(loggedIn);
        f.Api.Received().AccessToken = "remembered";
        Assert.False(f.Session.IsAdmin);

        var g = new Fixture();
        g.Settings.Current.RememberedLogins["localhost:5080"] = new RememberedLogin { UserName = "anna", Token = "expired" };
        g.Api.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns<Task<UserInfo>>(_ => throw new RpcException(new Status(StatusCode.Unauthenticated, "Your session has ended. Log in again.")));
        LoginViewModel expired = g.Create();
        await expired.InitializeAsync();
        Assert.Null(g.Session.UserName);
        Assert.Equal("anna", expired.UserName);
        Assert.Empty(g.Settings.Current.RememberedLogins);
    }

    [Fact]
    public void Server_address_rules_and_keys()
    {
        Assert.Null(LoginViewModel.ServerAddressError("localhost:5080"));
        Assert.Null(LoginViewModel.ServerAddressError("https://10.0.0.17:5080"));
        Assert.NotNull(LoginViewModel.ServerAddressError(""));
        Assert.NotNull(LoginViewModel.ServerAddressError("ftp://server"));
        Assert.Equal("localhost:5080", LoginViewModel.KeyOf("LOCALHOST:5080"));
        Assert.Equal("nvr:5080", LoginViewModel.KeyOf("https://nvr:5080/"));
    }

    [Fact]
    public void Pin_store_keeps_fingerprints_in_the_client_settings_and_forget_removes_the_remembered_login()
    {
        var settings = new InMemoryClientSettingsStore();
        var pinning = new ServerCertificatePinning(new ClientPinStore(settings));
        var address = new Uri("https://nvr:5080");
        settings.Current.RememberedLogins["nvr:5080"] = new RememberedLogin { UserName = "a", Token = "t" };
        pinning.Trust(address, Fingerprint);
        Assert.Equal(Fingerprint, settings.Current.PinnedServers["nvr:5080"]);
        pinning.Forget(address);
        Assert.Empty(settings.Current.PinnedServers);
        Assert.Empty(settings.Current.RememberedLogins);
    }

    [Fact]
    public async Task Users_card_follows_the_server_rules()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin }, "fake");
        var vm = new UsersViewModel(api, dialogs, session, NullLogger<UsersViewModel>.Instance);
        await vm.LoadAsync();
        Assert.Equal(5, vm.Users.Count);
        Assert.Equal("5 users, 2 administrators, 1 disabled", vm.Summary);
        UserRowViewModel self = vm.Users.Single(u => u.UserName == "admin");
        Assert.True(self.IsSelf);
        Assert.False(self.CanChange);

        Assert.False(vm.AddUserCommand.CanExecute(null));
        vm.NewUserName = "tech3";
        vm.NewPassword = "short";
        Assert.Equal("Use at least 10 characters.", vm.ErrorOf(nameof(vm.NewPassword)));
        vm.NewPassword = "Tech3-pass-123";
        await vm.AddUserCommand.ExecuteAsync(null);
        Assert.Contains(vm.Users, u => u.UserName == "tech3" && !u.IsAdmin);
        Assert.Equal("", vm.NewUserName);

        vm.NewUserName = "TECH3";
        vm.NewPassword = "Tech3-pass-123";
        await vm.AddUserCommand.ExecuteAsync(null);
        Assert.Equal("A user named TECH3 exists already.", vm.ErrorOf(nameof(vm.NewUserName)));

        UserRowViewModel tech3 = vm.Users.Single(u => u.UserName == "tech3");
        await vm.ToggleRoleCommand.ExecuteAsync(tech3);
        Assert.True(vm.Users.Single(u => u.UserName == "tech3").IsAdmin);
        await vm.ToggleDisabledCommand.ExecuteAsync(vm.Users.Single(u => u.UserName == "tech1"));
        Assert.True(vm.Users.Single(u => u.UserName == "tech1").IsDisabled);

        vm.OpenResetCommand.Execute(vm.Users.Single(u => u.UserName == "tech2"));
        Assert.Equal("New password for tech2", vm.ResetTitle);
        vm.ResetPassword = "Tech2-pass-new";
        vm.ResetConfirm = "different";
        Assert.False(vm.SaveResetCommand.CanExecute(null));
        vm.ResetConfirm = "Tech2-pass-new";
        await vm.SaveResetCommand.ExecuteAsync(null);
        Assert.False(vm.IsResetOpen);

        await vm.DeleteCommand.ExecuteAsync(vm.Users.Single(u => u.UserName == "contractor"));
        Assert.DoesNotContain(vm.Users, u => u.UserName == "contractor");
    }

    [Fact]
    public async Task Audit_tab_filters_five_thousand_entries_quickly()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AddAuditEntries(Enumerable.Range(0, 5000).Select(i => new AuditEntry
        {
            Time = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-i)),
            UserName = i % 10 == 0 ? "anna" : "tech1",
            ClientAddress = "10.0.0." + (i % 200),
            Action = i % 7 == 0 ? "Login failed" : "Started task",
            Target = "Restart",
            Detail = $"{i % 50} devices",
        }));
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin }, "fake");
        var audit = new AuditLogViewModel(api);
        var logs = new LogsViewModel(new LogStore(new ImmediateUiDispatcher()), audit, session);
        Assert.True(logs.CanSeeAudit);

        await logs.ShowAuditCommand.ExecuteAsync(null);
        Assert.True(logs.IsAuditTab);
        Assert.Equal(5014, audit.Entries.Count); // + the fake's sample entries
        Assert.Equal("5,014 entries", audit.StatusLine);
        Assert.True(audit.Entries[0].Id > audit.Entries[1].Id);

        var watch = Stopwatch.StartNew();
        audit.SearchText = "anna";
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 200, $"filter took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(500 + 4, audit.Entries.Count); // + the four sample entries of anna.berg
        Assert.StartsWith("504 match", audit.StatusLine, StringComparison.Ordinal);

        session.SignIn(new UserInfo { UserName = "tech1", Role = UserRole.Operator }, "fake");
        Assert.False(logs.CanSeeAudit);
        Assert.False(logs.IsAuditTab);
        Assert.Empty(audit.Entries);
    }

    // ------------------------------------------------------------------ headless screenshots

    [Fact]
    public async Task Login_first_admin_fingerprint_users_and_audit_render()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            // Login window with a recent server and a wrong password.
            var f = new Fixture();
            f.Settings.Current.RecentServers.AddRange(["localhost:5080", "nvr01.example.com:5080"]);
            f.Api.LoginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns<Task<LoginReply>>(_ => throw new RpcException(new Status(StatusCode.Unauthenticated, "The user name or password is wrong.")));
            LoginViewModel vm = f.Create();
            var login = new LoginWindow { DataContext = vm };
            login.Show();
            vm.UserName = "anna";
            vm.Password = "wrong-password";
            vm.RememberMe = true;
            await vm.SubmitCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            List<string> texts = login.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("The user name or password is wrong.", texts);
            Assert.Contains("Log in to OADM", texts);
            Assert.DoesNotContain(texts, t => t.EndsWith("...", StringComparison.Ordinal));
            Capture(login, outDir, "login-window.png");
            login.Close();

            // First administrator from another computer (setup code).
            var g = new Fixture();
            g.Api.GetAuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AuthStatus { NeedsFirstAdmin = true, SetupCodeRequired = true });
            LoginViewModel first = g.Create("nvr01.example.com:5080");
            var firstWindow = new LoginWindow { DataContext = first };
            firstWindow.Show();
            await first.InitializeAsync();
            first.UserName = "admin";
            first.Password = "Admin-pass-123";
            first.ConfirmPassword = "Admin-pass-12";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("The passwords do not match.", first.ErrorOf(nameof(first.ConfirmPassword)));
            Capture(firstWindow, outDir, "login-first-admin.png");
            firstWindow.Close();

            // Fingerprint confirmation: the shared confirmation window with the text the login asks with.
            var h = new Fixture();
            int calls = 0;
            h.Api.GetAuthStatusAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls == 1
                ? throw new RpcException(new Status(StatusCode.Unavailable, "ssl"))
                : Task.FromResult(new AuthStatus()));
            h.Trust.LastCheck.Returns(PinCheck.Unknown);
            h.Trust.PresentedFingerprint.Returns(Fingerprint);
            await h.Create().InitializeAsync();
            var (title, message, confirm) = Assert.Single(h.Confirmations);
            var owner = new Window { Width = 600, Height = 400 };
            owner.Show();
            var confirmWindow = new MessageWindow { Heading = title, Message = message, ConfirmText = confirm, CancelText = "Cancel" };
            confirmWindow.Show(owner);
            Dispatcher.UIThread.RunJobs();
            Capture(confirmWindow, outDir, "login-fingerprint-confirm.png");
            confirmWindow.Close();
            owner.Close();

            // Users card on the Settings page.
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var dialogs = Substitute.For<IDialogService>();
            var user = new UserSession();
            user.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin }, "fake");
            var users = new UsersViewModel(api, dialogs, user, NullLogger<UsersViewModel>.Instance);
            await users.LoadAsync();
            users.OpenResetCommand.Execute(users.Users.Single(u => u.UserName == "tech2"));
            users.NewUserName = "tech3";
            users.NewPassword = "short";
            var usersWindow = new Window { Width = 1000, Height = 900, Content = new ScrollViewer { Content = new UsersCardView { DataContext = users, Margin = new Avalonia.Thickness(16) } } };
            usersWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(usersWindow.GetVisualDescendants().OfType<DataGrid>().Single().GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "anna.berg");
            Capture(usersWindow, outDir, "settings-users-card.png");
            usersWindow.Close();

            // Audit tab of the Logs page.
            var audit = new AuditLogViewModel(api);
            var logs = new LogsViewModel(new LogStore(new ImmediateUiDispatcher()), audit, user);
            var logsWindow = new Window { Width = 1400, Height = 700, Content = new LogsView { DataContext = logs } };
            logsWindow.Show();
            await logs.ShowAuditCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            DataGrid auditGrid = logsWindow.GetVisualDescendants().OfType<DataGrid>().Single(d => d.Name == "AuditGrid");
            Assert.True(auditGrid.IsEffectivelyVisible);
            Assert.Equal(["Time", "User", "Client", "Action", "Target", "Detail"], auditGrid.Columns.Select(c => c.Header as string ?? "").ToArray());
            Capture(logsWindow, outDir, "logs-audit-tab.png");
            logsWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
