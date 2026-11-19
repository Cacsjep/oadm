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
using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>
/// The credential choice of a first password (the add page's first password editor and the context menu's Set password):
/// root's entries of the credential list for Administrators, the eye with the Credentials page's rules, the typed fields off
/// while an entry is chosen, and the entry's id (never its password) to the server.
/// </summary>
public sealed class FirstPasswordChoiceTests : IDisposable
{
    private static readonly DateTimeOffset Older = new(2026, 11, 2, 8, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 11, 18, 14, 2, 0, TimeSpan.Zero);

    private readonly FakeOadmApi _fake = new(TimeSpan.FromMilliseconds(5));
    private readonly UiThread _ui = new();

    public void Dispose()
    {
        _ui.Dispose();
        _fake.Dispose();
    }

    private static IOadmApi ApiWithList()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListCredentialsAsync(Arg.Any<CancellationToken>()).Returns(new List<CredentialEntry>
        {
            new() { Id = "new-root", UserName = "root", Created = Timestamp.FromDateTimeOffset(Newer) },
            new() { Id = "operator", UserName = "operator", Created = Timestamp.FromDateTimeOffset(Older) },
            new() { Id = "old-root", UserName = "root", Created = Timestamp.FromDateTimeOffset(Older) },
        });
        api.RevealCredentialAsync("old-root", Arg.Any<CancellationToken>()).Returns("Stored-root-1");
        return api;
    }

    private static List<DeviceRowViewModel> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new DeviceRowViewModel(TestSupport.Device(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"B8A44F11AA{i:D2}", $"10.0.0.{39 + i}", "AXIS F9111", DeviceStatus.PasswordNotSet)))
            .ToList();

    // ------------------------------------------------------------------ the choice

    [Fact]
    public async Task An_administrator_chooses_from_the_entries_of_root_oldest_first_with_the_time_they_were_added()
    {
        IOadmApi api = ApiWithList();
        var choice = new FirstPasswordChoice(api, isAdministrator: true);
        Assert.False(choice.IsAvailable); // nothing read yet

        await choice.LoadAsync(CancellationToken.None);

        Assert.True(choice.IsAvailable);
        Assert.Equal([null, "old-root", "new-root"], choice.Options.Select(o => o.CredentialId));
        Assert.Equal(FirstPasswordChoice.TypeNew, choice.Options[0].Label);
        Assert.Equal("root", choice.Options[1].Label);
        Assert.Equal(CredentialTexts.Added(Older), choice.Options[1].AddedText);
        Assert.Equal(CredentialTexts.Added(Newer), choice.Options[2].AddedText);
        Assert.False(choice.UsesCredential); // the typed password stays the default
        Assert.True(choice.CanReveal);
    }

    [Fact]
    public async Task Other_users_never_see_the_choice_nor_read_the_credential_list()
    {
        IOadmApi api = ApiWithList();
        var choice = new FirstPasswordChoice(api, isAdministrator: false);

        await choice.LoadAsync(CancellationToken.None);

        Assert.False(choice.IsAvailable);
        Assert.False(choice.CanReveal);
        Assert.False(choice.ShowsStoredPassword);
        await api.DidNotReceive().ListCredentialsAsync(Arg.Any<CancellationToken>());
        await choice.ToggleRevealCommand.ExecuteAsync(null);
        await api.DidNotReceive().RevealCredentialAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_password_of_root_in_the_list_the_choice_stays_hidden()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListCredentialsAsync(Arg.Any<CancellationToken>()).Returns(new List<CredentialEntry> { new() { Id = "a", UserName = "admin" } });
        var choice = new FirstPasswordChoice(api, isAdministrator: true);

        await choice.LoadAsync(CancellationToken.None);

        Assert.False(choice.IsAvailable);
    }

    [Fact]
    public async Task The_eye_shows_the_chosen_password_and_forgets_it_on_the_next_click_or_choice()
    {
        IOadmApi api = ApiWithList();
        var choice = new FirstPasswordChoice(api, isAdministrator: true);
        await choice.LoadAsync(CancellationToken.None);
        choice.Select("old-root");
        Assert.True(choice.ShowsStoredPassword);
        Assert.Equal(CredentialTexts.Masked, choice.PasswordText);

        await choice.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Equal("Stored-root-1", choice.PasswordText);
        await choice.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Equal(CredentialTexts.Masked, choice.PasswordText);
        await api.Received(1).RevealCredentialAsync("old-root", Arg.Any<CancellationToken>());

        await choice.ToggleRevealCommand.ExecuteAsync(null);
        choice.Select("new-root");
        Assert.False(choice.IsRevealed);

        api.RevealCredentialAsync("new-root", Arg.Any<CancellationToken>()).Returns<string>(_ => throw new RpcException(new Status(StatusCode.PermissionDenied, "Administrators only.")));
        await choice.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Equal(CredentialTexts.ShowFailed + "Administrators only.", choice.FieldError);
        choice.Select(null);
        Assert.Null(choice.FieldError);
        Assert.False(choice.ShowsStoredPassword);
    }

    // ------------------------------------------------------------------ the add page

    [Fact]
    public Task The_add_page_sends_the_chosen_entry_and_turns_the_typed_fields_off() => _ui.RunAsync(async () =>
    {
        await using var page = new AddDevicesViewModel(_fake, _ui, NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan);
        await page.OpenAsync();
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));
        DiscoveredRowViewModel simple = page.Rows.Single(r => r.Address == "10.0.0.90");
        DiscoveredRowViewModel complex = page.Rows.Single(r => r.Address == "10.0.0.91");

        page.OpenEditorCommand.Execute(complex);
        await page.PasswordChoiceLoad!;
        Assert.True(page.PasswordChoice.IsAvailable);
        Assert.True(page.TypesPassword);
        page.NewPassword = "short";
        page.ConfirmPassword = "other";
        Assert.False(page.ApplyPasswordCommand.CanExecute(null));

        FirstPasswordOption root = page.PasswordChoice.Options.Single(o => o.CredentialId is not null);
        page.PasswordChoice.SelectedOption = root;

        // A chosen entry replaces the typed password: fields cleared and off, no error, Apply ready.
        Assert.False(page.TypesPassword);
        Assert.Equal("", page.NewPassword);
        Assert.Equal("", page.ConfirmPassword);
        Assert.Null(page.ErrorOf(nameof(page.NewPassword)));
        Assert.Null(page.ApplyPasswordBlockedReason);
        Assert.True(page.ApplyPasswordCommand.CanExecute(null));
        page.UseForAllFactoryDefault = true;
        page.ApplyPasswordCommand.Execute(null);

        Assert.All(new[] { simple, complex }, r =>
        {
            Assert.Equal(root.CredentialId, r.PendingCredentialId);
            Assert.Null(r.PendingPassword);
            Assert.True(r.ShowPasswordReady);
            Assert.True(r.IsSelected);
        });
        Assert.True(page.PasswordChoice.IsAvailable);
        Assert.False(page.PasswordChoice.UsesCredential); // the closed editor starts typed again

        // Change: the editor opens with the entry chosen.
        page.OpenEditorCommand.Execute(complex);
        Assert.Equal(root.CredentialId, page.PasswordChoice.CredentialId);
        page.CancelEditorCommand.Execute(null);

        // The add sends the entry's id (the fake sets its stored password like the server); the client never had it.
        await page.AddCommand.ExecuteAsync(null);
        IReadOnlyList<Device> devices = await _fake.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Ok, devices.Single(d => d.Address == "10.0.0.91").Status);
        Assert.Equal(DeviceStatus.Ok, devices.Single(d => d.Address == "10.0.0.90").Status);
    });

    [Fact]
    public async Task Follow_clears_the_typed_fields_for_an_entry_and_checks_them_again_without_showing_an_error()
    {
        var choice = new FirstPasswordChoice(ApiWithList(), isAdministrator: true);
        await choice.LoadAsync(CancellationToken.None);
        var password = "typed";
        var changed = 0;
        var validation = new Oadm.Sdk.Client.Validation.FormValidator(_ => { });
        validation.Rule("Password", () => choice.UsesCredential ? null : password.Length < 8 ? "Too short." : null);
        validation.SetServerError("Password", "The device refused it.");
        choice.Follow(validation, () => password = "", () => changed++, "Password");

        choice.Select("new-root");
        Assert.Equal(("", 1), (password, changed));
        Assert.Null(validation["Password"]);
        Assert.True(validation.IsValid);

        choice.Select(null); // typed again: the empty field is an error, not shown before it is edited
        Assert.Equal(2, changed);
        Assert.Null(validation["Password"]);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public Task A_page_of_another_user_shows_no_choice() => _ui.RunAsync(async () =>
    {
        await using var page = new AddDevicesViewModel(_fake, _ui, NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan) { CanSaveToCredentialList = false };
        await page.OpenAsync();
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));

        page.OpenEditorCommand.Execute(page.Rows.Single(r => r.Address == "10.0.0.91"));
        await page.PasswordChoiceLoad!;

        Assert.False(page.PasswordChoice.IsAvailable);
        Assert.False(page.PasswordChoice.CanReveal);
        Assert.True(page.TypesPassword);
    });

    // ------------------------------------------------------------------ Set password (managed devices)

    [Fact]
    public async Task Set_password_with_an_entry_calls_the_server_with_its_id_and_shows_the_refusal_below_the_choice()
    {
        IOadmApi api = ApiWithList();
        var reply = new SetFirstPasswordReply();
        reply.Results.Add(new SetFirstPasswordResult { DeviceId = "1", Ok = true });
        api.SetFirstPasswordFromListAsync(Arg.Any<IReadOnlyCollection<string>>(), "old-root", Arg.Any<CancellationToken>()).Returns(reply);
        api.SetFirstPasswordFromListAsync(Arg.Any<IReadOnlyCollection<string>>(), "new-root", Arg.Any<CancellationToken>())
            .Returns<SetFirstPasswordReply>(_ => throw new RpcException(new Status(StatusCode.NotFound, Oadm.Sdk.Devices.DeviceFirstSetupTexts.CredentialGone)));
        var vm = new DeviceSetPasswordViewModel(api, Rows(1), isAdministrator: true);
        await vm.LoadAsync(CancellationToken.None);
        vm.Password = "typed";
        Assert.False(vm.SetPasswordCommand.CanExecute(null));

        vm.PasswordChoice.Select("new-root");
        Assert.False(vm.TypesPassword);
        Assert.False(vm.CanType);
        Assert.Equal("", vm.Password);
        Assert.True(vm.SetPasswordCommand.CanExecute(null));
        await vm.SetPasswordCommand.ExecuteAsync(null);
        Assert.Equal("The password could not be set: " + Oadm.Sdk.Devices.DeviceFirstSetupTexts.CredentialGone, vm.PasswordChoice.FieldError);
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));

        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        vm.PasswordChoice.Select("old-root");
        Assert.Null(vm.PasswordChoice.FieldError);
        await vm.SetPasswordCommand.ExecuteAsync(null);

        Assert.True(closed);
        await api.DidNotReceive().SetFirstPasswordAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_password_of_another_user_has_no_choice()
    {
        IOadmApi api = ApiWithList();
        var vm = new DeviceSetPasswordViewModel(api, Rows(1));

        await vm.LoadAsync(CancellationToken.None);

        Assert.False(vm.PasswordChoice.IsAvailable);
        Assert.True(vm.TypesPassword);
        await api.DidNotReceive().ListCredentialsAsync(Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ headless screenshots

    [Fact]
    public async Task The_first_password_editor_and_set_password_render_the_choice_right_of_the_fields()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            using var fake = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var page = new AddDevicesViewModel(fake, new AvaloniaUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan);
            var window = new AddDevicesWindow { DataContext = page, Width = 1040 };
            window.Show();
            await page.OpenAsync();
            await PumpUntilAsync(() => page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));
            page.OpenEditorCommand.Execute(page.Rows.Single(r => r.Address == "10.0.0.91"));
            await page.PasswordChoiceLoad!;
            await PumpUntilAsync(() => true);

            FirstPasswordChoiceBox box = window.GetVisualDescendants().OfType<FirstPasswordChoiceBox>().Single();
            Assert.True(box.IsEffectivelyVisible);
            Capture(window, outDir, "client-add-password-choice.png");

            page.PasswordChoice.Select(page.PasswordChoice.Options.Single(o => o.CredentialId is not null).CredentialId);
            await page.PasswordChoice.ToggleRevealCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => page.PasswordChoice.IsRevealed);
            List<Oadm.Sdk.Client.Controls.PasswordBox> fields = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.PasswordBox>().Where(p => p.IsEffectivelyVisible).ToList();
            Assert.Equal(2, fields.Count);
            Assert.All(fields, f => Assert.False(f.IsEffectivelyEnabled));
            Assert.Contains("Fake-root-pass1", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""));
            Capture(window, outDir, "client-add-password-choice-revealed.png");
            await page.DisposeAsync();
            Assert.False(page.PasswordChoice.IsRevealed); // forgotten with the page
            window.Close();

            var vm = new DeviceSetPasswordViewModel(fake, Rows(1), isAdministrator: true);
            var dialog = new DeviceSetPasswordWindow();
            dialog.Attach(vm);
            dialog.Show();
            await vm.LoadAsync(CancellationToken.None);
            await PumpUntilAsync(() => vm.PasswordChoice.IsAvailable);
            Capture(dialog, outDir, "client-set-password-choice.png");
            dialog.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static async Task PumpUntilAsync(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string what = "")
    {
        DateTime end = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        while (!condition() && DateTime.UtcNow < end);

        Assert.True(condition(), "UI condition not met in time: " + what);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
