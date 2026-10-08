using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>"Log in" for managed devices whose stored credentials are rejected: context menu entry and dialog.</summary>
public sealed class DeviceLoginTests
{
    private static DevicesFixture CreateWithDevices(UserSession? session = null)
    {
        var f = new DevicesFixture(session: session);
        f.SeedDevices(
            TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V", DeviceStatus.CredentialsRequired),
            TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II"),
            TestSupport.Device("3", "B8A44F2E7A90", "10.0.0.23", "AXIS P1468-LE", DeviceStatus.CredentialsRequired),
            TestSupport.Device("4", "00408CA1B2C3", "10.0.0.30", "AXIS Q1798-LE", DeviceStatus.PasswordNotSet));
        return f;
    }

    private static UserSession Session(bool admin)
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = admin ? "admin" : "tech1", Role = admin ? UserRole.Admin : UserRole.Operator }, "localhost:5080");
        return session;
    }

    private static List<DeviceRowViewModel> Rows(int count, string status = "CredentialsRequired") =>
        Enumerable.Range(1, count)
            .Select(i => new DeviceRowViewModel(TestSupport.Device(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"B8A44F6313{i:D2}", $"10.0.0.{47 + i}", "AXIS P3265-V",
                Enum.Parse<DeviceStatus>(status))))
            .ToList();

    private static DeviceLogInResult Ok(string id) => new() { DeviceId = id, Ok = true };

    private static DeviceLogInResult Rejected(string id) => new() { DeviceId = id, Rejected = true, Message = "The user name or password is wrong." };

    private static DeviceLogInResult Failed(string id, string message) => new() { DeviceId = id, Message = message };

    private static void Reply(IOadmApi api, params DeviceLogInResult[] results)
    {
        var reply = new DeviceLogInReply();
        reply.Results.AddRange(results);
        api.LogInDevicesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(reply);
    }

    // ------------------------------------------------------------------ context menu

    [Fact]
    public void Log_in_appears_only_when_the_selection_has_a_device_that_rejects_its_credentials()
    {
        using DevicesFixture f = CreateWithDevices();

        f.Select("2");
        Assert.DoesNotContain("Log in", f.Devices.ContextMenuEntries.Select(e => e.Header));

        // Factory default is not a login problem: the add page sets the first password.
        f.Select("4");
        Assert.DoesNotContain("Log in", f.Devices.ContextMenuEntries.Select(e => e.Header));

        f.Select("2", "3");
        Assert.Equal(["Open web interface", "Refresh", "Log in", "Tags"], f.Devices.ContextMenuEntries.Select(e => e.Header).Take(4).ToArray());
        Assert.Equal(["-", "Remove"], f.Devices.ContextMenuEntries.Select(e => e.Header).TakeLast(2).ToArray()); // Remove last, after its own separator
        MenuEntryViewModel login = f.Devices.ContextMenuEntries[2];
        Assert.Equal("key", login.IconKey);
        Assert.Same(f.Devices.LogInCommand, login.Command);
        Assert.Equal(["3"], f.Devices.LoginTargets().Select(d => d.Id));
    }

    [Fact]
    public void Log_in_follows_the_status_of_the_selected_devices()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Select("2", "3");
        Assert.Contains("Log in", f.Devices.ContextMenuEntries.Select(e => e.Header));

        // The refresh after a login sets the device back to Ok: the entry goes away.
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device("3", "B8A44F2E7A90", "10.0.0.23", "AXIS P1468-LE") }]);
        Assert.DoesNotContain("Log in", f.Devices.ContextMenuEntries.Select(e => e.Header));

        // A poll finds rejected credentials on a selected device: the entry comes back.
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II", DeviceStatus.CredentialsRequired) }]);
        Assert.Contains("Log in", f.Devices.ContextMenuEntries.Select(e => e.Header));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Log_in_opens_the_dialog_for_the_affected_devices_only(bool admin)
    {
        using DevicesFixture f = CreateWithDevices(Session(admin));
        DeviceLoginViewModel? shown = null;
        f.Dialogs.ShowDeviceLoginAsync(Arg.Do<DeviceLoginViewModel>(vm => shown = vm)).Returns(false);
        f.Select("1", "2", "3", "4");

        await f.Devices.LogInCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(["1", "3"], shown.Devices.Select(d => d.Id));
        Assert.Equal("2 devices reject the stored credentials.", shown.Intro);
        Assert.Equal(admin, shown.CanSaveToCredentialList);
    }

    [Fact]
    public async Task A_full_credential_list_is_told_after_the_dialog_closed()
    {
        using DevicesFixture f = CreateWithDevices(Session(admin: true));
        f.Dialogs.ShowDeviceLoginAsync(Arg.Any<DeviceLoginViewModel>()).Returns(async call =>
        {
            var vm = call.Arg<DeviceLoginViewModel>();
            var reply = new DeviceLogInReply { CredentialListNote = "The credential list is full (20 entries); the login was not saved there." };
            reply.Results.Add(Ok("3"));
            f.Api.LogInDevicesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(reply);
            vm.Password = "secret";
            bool closed = false;
            vm.CloseRequested += (_, ok) => closed = ok;
            await vm.LogInCommand.ExecuteAsync(null);
            return closed;
        });
        f.Select("3");

        await f.Devices.LogInCommand.ExecuteAsync(null);

        await f.Dialogs.Received(1).ShowMessageAsync("Log in", "The credential list is full (20 entries); the login was not saved there.");
    }

    // ------------------------------------------------------------------ dialog view model

    [Fact]
    public void One_device_names_the_device_and_the_form_needs_user_name_and_password()
    {
        var vm = new DeviceLoginViewModel(Substitute.For<IOadmApi>(), Rows(1), canSaveToCredentialList: true);

        Assert.Equal("10.0.0.48 (AXIS P3265-V) rejects the stored credentials.", vm.Intro);
        Assert.Equal("root", vm.UserName);
        Assert.True(vm.SaveToCredentialList);
        Assert.False(vm.LogInCommand.CanExecute(null));
        Assert.Equal("Enter the password.", vm.FormError);
        Assert.Null(vm.ErrorOf(nameof(vm.Password))); // untouched form: no error shown

        vm.UserName = " ";
        Assert.Equal("Enter a user name.", vm.ErrorOf(nameof(vm.UserName)));
        vm.UserName = "root";
        vm.Password = "secret";
        Assert.True(vm.LogInCommand.CanExecute(null));
        Assert.Null(vm.FormError);
    }

    [Fact]
    public async Task The_stored_user_name_prefills_the_form_unless_the_user_typed_one()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetCredentialUserNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns("service");
        var vm = new DeviceLoginViewModel(api, Rows(2), true);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal("service", vm.UserName);

        var typed = new DeviceLoginViewModel(api, Rows(2), true) { UserName = "anna" };
        await typed.LoadAsync(CancellationToken.None);
        Assert.Equal("anna", typed.UserName);

        api.GetCredentialUserNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns("");
        var differing = new DeviceLoginViewModel(api, Rows(2), true);
        await differing.LoadAsync(CancellationToken.None);
        Assert.Equal("root", differing.UserName);
    }

    [Fact]
    public async Task All_devices_accept_the_login_one_call_and_the_dialog_closes()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Ok("1"), Ok("2"), Ok("3"));
        var vm = new DeviceLoginViewModel(api, Rows(3), canSaveToCredentialList: true) { Password = "secret" };
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.LogInCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(3, vm.LoggedInCount);
        await api.Received(1).LogInDevicesAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "1", "2", "3" })), "root", "secret", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operators_never_ask_to_save_in_the_credential_list()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Ok("1"));
        var vm = new DeviceLoginViewModel(api, Rows(1), canSaveToCredentialList: false) { Password = "secret" };
        Assert.True(vm.SaveToCredentialList); // the hidden check box does not count

        await vm.LogInCommand.ExecuteAsync(null);

        await api.Received(1).LogInDevicesAsync(Arg.Any<IReadOnlyCollection<string>>(), "root", "secret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_device_that_rejects_the_login_shows_the_reason_below_the_password()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Rejected("1"));
        var vm = new DeviceLoginViewModel(api, Rows(1), true) { Password = "wrong" };
        bool closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        await vm.LogInCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Equal("The user name or password is wrong.", vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.LogInCommand.CanExecute(null));

        // Typing a new password clears it.
        vm.Password = "other";
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.True(vm.LogInCommand.CanExecute(null));
    }

    [Fact]
    public async Task Several_devices_keep_only_those_that_rejected_it()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Rejected("1"), Rejected("2"), Ok("3"), Ok("4"), Ok("5"));
        var vm = new DeviceLoginViewModel(api, Rows(5), true) { Password = "secret" };

        await vm.LogInCommand.ExecuteAsync(null);

        Assert.Equal("2 of 5 devices rejected it: 10.0.0.48, 10.0.0.49", vm.ErrorOf(nameof(vm.Password)));
        Assert.Equal(["1", "2"], vm.Devices.Select(d => d.Id));
        Assert.Equal("2 devices reject the stored credentials.", vm.Intro);
        Assert.Equal(3, vm.LoggedInCount);

        // The next try goes to the remaining devices only.
        Reply(api, Ok("1"), Ok("2"));
        vm.Password = "second";
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        await vm.LogInCommand.ExecuteAsync(null);
        Assert.True(closed);
        await api.Received(1).LogInDevicesAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "1", "2" })), "root", "second", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Other_reasons_are_named_per_device_and_long_lists_are_shortened()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Rejected("1"), Failed("2", "Unreachable - No route to host"), Ok("3"));
        var vm = new DeviceLoginViewModel(api, Rows(3), true) { Password = "secret" };
        await vm.LogInCommand.ExecuteAsync(null);
        Assert.Equal("2 of 3 devices failed: 10.0.0.48 (wrong user name or password), 10.0.0.49 (Unreachable - No route to host)", vm.ErrorOf(nameof(vm.Password)));

        Reply(api, [.. Enumerable.Range(1, 8).Select(i => Rejected(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
        var many = new DeviceLoginViewModel(api, Rows(8), true) { Password = "secret" };
        await many.LogInCommand.ExecuteAsync(null);
        Assert.Equal("8 of 8 devices rejected it: 10.0.0.48, 10.0.0.49, 10.0.0.50, 10.0.0.51, 10.0.0.52 +3 more", many.ErrorOf(nameof(many.Password)));
    }

    [Fact]
    public async Task A_server_error_shows_below_the_password()
    {
        var api = Substitute.For<IOadmApi>();
        api.LogInDevicesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeviceLogInReply>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "Server not reachable")));
        var vm = new DeviceLoginViewModel(api, Rows(1), true) { Password = "secret" };

        await vm.LogInCommand.ExecuteAsync(null);

        Assert.Equal("The login could not be checked: Server not reachable", vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.IsBusy);
    }

    // ------------------------------------------------------------------ headless screenshots

    [Fact]
    public async Task Log_in_dialog_renders()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = Substitute.For<IOadmApi>();
            api.GetCredentialUserNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns("root");

            // One device, administrator: the check box is shown.
            var single = new DeviceLoginViewModel(api, Rows(1), canSaveToCredentialList: true);
            var window = new DeviceLoginWindow();
            window.Attach(single);
            window.Show();
            single.Password = "Secret-pass1";
            Dispatcher.UIThread.RunJobs();
            List<string> texts = Texts(window);
            Assert.Contains("10.0.0.48 (AXIS P3265-V) rejects the stored credentials.", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible && Equals(c.Content, "Save to credential list"));
            Assert.Equal(460, window.Bounds.Width);
            Capture(window, outDir, "client-device-login.png");
            window.Close();

            // Five devices, two rejected it.
            Reply(api, Rejected("1"), Rejected("2"), Ok("3"), Ok("4"), Ok("5"));
            var multi = new DeviceLoginViewModel(api, Rows(5), canSaveToCredentialList: true);
            var failed = new DeviceLoginWindow();
            failed.Attach(multi);
            failed.Show();
            multi.Password = "Secret-pass1";
            await multi.LogInCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            texts = Texts(failed);
            Assert.Contains("2 of 5 devices rejected it: 10.0.0.48, 10.0.0.49", texts);
            Assert.Contains("2 devices reject the stored credentials.", texts);
            Assert.DoesNotContain(texts, t => t.EndsWith("...", StringComparison.Ordinal));
            Capture(failed, outDir, "client-device-login-failed.png");
            failed.Close();

            // Operator: no credential list check box.
            var op = new DeviceLoginViewModel(api, Rows(1), canSaveToCredentialList: false);
            var operatorWindow = new DeviceLoginWindow();
            operatorWindow.Attach(op);
            operatorWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(operatorWindow.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible);
            operatorWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

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
