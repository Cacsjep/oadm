using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// Choose credentials before the add devices page (docs/spec/add-devices.md, decision 2026-11-25): only for an
/// Administrator with more entries than one add tries, at most 4 ticked in the list's order, the eye, Add credential, the
/// choice remembered for the client session and passed on every session start of the add page.
/// </summary>
public sealed class CredentialChoiceTests : IDisposable
{
    private readonly FakeOadmApi _fake = new(TimeSpan.FromMilliseconds(5));
    private readonly UiThread _ui = new();

    public CredentialChoiceTests() => _fake.SeedMoreCredentials(); // 5 entries: root, operator, admin, service, installer

    public void Dispose()
    {
        _ui.Dispose();
        _fake.Dispose();
    }

    private async Task<IReadOnlyList<CredentialEntry>> EntriesAsync() => await _fake.ListCredentialsAsync(CancellationToken.None);

    private async Task<CredentialChoiceViewModel> CreateAsync(params string[] remembered) =>
        new(_fake, await EntriesAsync(), remembered);

    private static string[] Users(CredentialChoiceViewModel vm) =>
        [.. vm.Items.Where(i => i.IsChecked).Select(i => i.Entry.UserName)];

    private static FakeOadmApi MoreCredentials()
    {
        var fake = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        fake.SeedMoreCredentials();
        return fake;
    }

    private static UserSession Session(bool admin)
    {
        var session = new UserSession();
        UserRole role = admin ? UserRole.Admin : UserRole.Operator;
        session.SignIn(new UserInfo { UserName = "tech", Role = role, RoleName = role.ToString() }, "fake");
        return session;
    }

    [Theory]
    [InlineData(true, 5, true)]
    [InlineData(true, 4, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 5, false)]
    public void The_dialog_applies_only_to_administrators_with_more_than_4_entries(bool admin, int count, bool expected) =>
        Assert.Equal(expected, CredentialChoiceViewModel.Applies(admin, count));

    [Fact]
    public async Task The_first_4_are_ticked_when_nothing_was_chosen_yet()
    {
        CredentialChoiceViewModel vm = await CreateAsync();

        Assert.Equal(5, vm.Items.Count);
        Assert.Equal(["root", "operator", "admin", "service"], Users(vm));
        Assert.True(vm.ContinueCommand.CanExecute(null));
        Assert.Null(vm.ChoiceError);
    }

    [Fact]
    public async Task The_remembered_choice_is_ticked_in_the_list_order_and_gone_entries_fall_back_to_the_first_4()
    {
        IReadOnlyList<CredentialEntry> entries = await EntriesAsync();

        var vm = new CredentialChoiceViewModel(_fake, entries, [entries[4].Id, entries[1].Id, "removed-meanwhile"]);
        Assert.Equal(["operator", "installer"], Users(vm));
        Assert.Equal([entries[1].Id, entries[4].Id], vm.ChosenIds); // the list's order is the try order

        var gone = new CredentialChoiceViewModel(_fake, entries, ["removed-meanwhile"]);
        Assert.Equal(["root", "operator", "admin", "service"], Users(gone));
    }

    [Fact]
    public async Task At_most_4_can_be_ticked_and_none_blocks_Continue()
    {
        CredentialChoiceViewModel vm = await CreateAsync();

        // 4 ticked: the fifth box is off, the ticked ones stay changeable.
        Assert.False(vm.Items[4].CanCheck);
        Assert.All(vm.Items.Take(4), i => Assert.True(i.CanCheck));

        vm.Items[0].IsChecked = false;
        Assert.True(vm.Items[4].CanCheck);
        vm.Items[4].IsChecked = true;
        Assert.False(vm.Items[0].CanCheck);
        Assert.Equal(["operator", "admin", "service", "installer"], Users(vm));

        foreach (CredentialChoiceItem item in vm.Items)
        {
            item.IsChecked = false;
        }

        Assert.False(vm.ContinueCommand.CanExecute(null));
        Assert.Equal(CredentialChoiceViewModel.NoneChosenText, vm.ChoiceError);
        Assert.Equal(CredentialChoiceViewModel.NoneChosenText, vm.ContinueBlockedReason);
        Assert.All(vm.Items, i => Assert.True(i.CanCheck));
    }

    [Fact]
    public async Task Continue_and_Cancel_close_the_dialog()
    {
        CredentialChoiceViewModel vm = await CreateAsync();
        var results = new List<bool>();
        vm.CloseRequested += (_, ok) => results.Add(ok);

        vm.ContinueCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task Show_passwords_shows_them_all_and_hides_them_again()
    {
        CredentialChoiceViewModel vm = await CreateAsync();
        CredentialChoiceItem root = vm.Items[0];
        Assert.Equal("Show passwords", vm.PasswordsButtonText);

        await vm.TogglePasswordsCommand.ExecuteAsync(null);
        Assert.Equal("Fake-root-pass1", root.Entry.PasswordText);
        Assert.All(vm.Items, i => Assert.True(i.Entry.IsRevealed));
        Assert.Equal("Hide passwords", vm.PasswordsButtonText);

        // A new entry shows its password too while they are shown.
        vm.StartAddCommand.Execute(null);
        vm.NewUserName = "viewer";
        vm.NewPassword = "Viewer-pass1";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal("Viewer-pass1", vm.Items[^1].Entry.PasswordText);

        await vm.TogglePasswordsCommand.ExecuteAsync(null);
        Assert.All(vm.Items, i => Assert.Equal(Oadm.Sdk.Client.CredentialTexts.Masked, i.Entry.PasswordText));

        await vm.TogglePasswordsCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        Assert.All(vm.Items, i => Assert.False(i.Entry.IsRevealed)); // forgotten with the dialog
        Assert.False(vm.ArePasswordsShown);
    }

    [Fact]
    public async Task A_failed_reveal_shows_below_the_list()
    {
        IOadmApi api = Substitute.For<IOadmApi>();
        api.RevealCredentialAsync(default!, default).ThrowsAsyncForAnyArgs(new RpcException(new Status(StatusCode.PermissionDenied, "Administrators only.")));
        var vm = new CredentialChoiceViewModel(api, await EntriesAsync(), []);

        await vm.TogglePasswordsCommand.ExecuteAsync(null);

        Assert.Equal(Oadm.Sdk.Client.CredentialTexts.ShowFailed + "Administrators only.", vm.ErrorText);
        Assert.All(vm.Items, i => Assert.False(i.Entry.IsRevealed));
        Assert.False(vm.ArePasswordsShown);
    }

    [Fact]
    public async Task Add_credential_saves_it_to_the_list_and_ticks_it_in_place_of_the_last_ticked()
    {
        CredentialChoiceViewModel vm = await CreateAsync();

        vm.StartAddCommand.Execute(null);
        Assert.True(vm.IsAdding);
        vm.NewUserName = "viewer";
        vm.NewPassword = "Viewer-pass1";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.False(vm.IsAdding);
        Assert.Equal("", vm.NewPassword);
        Assert.Equal(6, vm.Items.Count);
        Assert.Equal(["root", "operator", "admin", "viewer"], Users(vm)); // 4 were ticked: the last of them made room
        Assert.Contains(await EntriesAsync(), e => e.UserName == "viewer" && e.Id == vm.Items[5].Entry.Id);
        Assert.Equal("Viewer-pass1", await _fake.RevealCredentialAsync(vm.Items[5].Entry.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Add_credential_ticks_the_new_entry_when_there_is_room()
    {
        CredentialChoiceViewModel vm = await CreateAsync();
        vm.Items[3].IsChecked = false;

        vm.StartAddCommand.Execute(null);
        vm.NewUserName = "viewer";
        vm.NewPassword = "Viewer-pass1";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(["root", "operator", "admin", "viewer"], Users(vm));
    }

    [Fact]
    public async Task Add_credential_validates_like_the_Credentials_page()
    {
        CredentialChoiceViewModel vm = await CreateAsync();
        vm.StartAddCommand.Execute(null);

        Assert.Null(vm.ErrorOf(nameof(CredentialChoiceViewModel.NewPassword))); // untouched form: no error yet
        Assert.False(vm.AddCommand.CanExecute(null));
        Assert.Equal("Enter the password.", vm.AddBlockedReason);

        vm.NewUserName = " ";
        Assert.Equal("Enter a user name.", vm.ErrorOf(nameof(CredentialChoiceViewModel.NewUserName)));

        vm.CancelAddCommand.Execute(null);
        Assert.False(vm.IsAdding);
        Assert.Null(vm.ErrorOf(nameof(CredentialChoiceViewModel.NewUserName)));
    }

    [Fact]
    public async Task A_refused_add_shows_below_the_user_name()
    {
        IOadmApi api = Substitute.For<IOadmApi>();
        api.AddCredentialAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new RpcException(new Status(StatusCode.ResourceExhausted, "The credential list is full.")));
        var vm = new CredentialChoiceViewModel(api, await EntriesAsync(), []);

        vm.StartAddCommand.Execute(null);
        vm.NewPassword = "x";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("Adding failed: The credential list is full.", vm.ErrorOf(nameof(CredentialChoiceViewModel.NewUserName)));
        Assert.True(vm.IsAdding);
        Assert.Equal(5, vm.Items.Count);
    }

    // ------------------------------------------------------------ before the add page

    [Fact]
    public async Task An_administrator_with_more_than_4_entries_chooses_first_and_the_choice_is_remembered()
    {
        using var f = new DevicesFixture(MoreCredentials(), session: Session(admin: true)); // the fixture disposes its server
        IReadOnlyList<CredentialEntry> entries = await f.Api.ListCredentialsAsync(CancellationToken.None);
        var shown = new List<CredentialChoiceViewModel>();
        f.Dialogs.ShowCredentialChoiceAsync(default!).ReturnsForAnyArgs(call =>
        {
            var choice = call.Arg<CredentialChoiceViewModel>();
            shown.Add(choice);
            choice.Items[0].IsChecked = false;
            choice.Items[4].IsChecked = true;
            return true;
        });
        var pages = new List<AddDevicesViewModel>();
        f.Dialogs.ShowAddDevicesAsync(default!).ReturnsForAnyArgs(call =>
        {
            pages.Add(call.Arg<AddDevicesViewModel>());
            return true;
        });

        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddManually);
        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddScan);

        string[] chosen = [entries[1].Id, entries[2].Id, entries[3].Id, entries[4].Id];
        Assert.Equal(2, shown.Count);
        Assert.Equal(2, pages.Count);
        Assert.Equal(chosen, pages[0].CredentialIds);
        Assert.Equal(["operator", "admin", "service", "installer"], shown[1].Items.Where(i => i.IsChecked).Select(i => i.Entry.UserName));
    }

    [Fact]
    public async Task Cancel_opens_no_add_page()
    {
        using var f = new DevicesFixture(MoreCredentials(), session: Session(admin: true));
        f.Dialogs.ShowCredentialChoiceAsync(default!).ReturnsForAnyArgs(false);

        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddIpRange);

        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowAddDevicesAsync(default!);
    }

    [Fact]
    public async Task Four_entries_or_fewer_open_the_add_page_without_asking()
    {
        using var f = new DevicesFixture(new FakeOadmApi(TimeSpan.FromMilliseconds(5)), session: Session(admin: true)); // root and operator
        f.Dialogs.ShowAddDevicesAsync(default!).ReturnsForAnyArgs(true);

        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddScan);

        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowCredentialChoiceAsync(default!);
        await f.Dialogs.Received(1).ShowAddDevicesAsync(Arg.Is<AddDevicesViewModel>(p => p.CredentialIds.Count == 0));
    }

    [Fact]
    public async Task A_user_who_cannot_read_the_credential_list_is_never_asked()
    {
        IOadmApi api = Substitute.For<IOadmApi>();
        using var f = new DevicesFixture(api, session: Session(admin: false));
        f.Dialogs.ShowAddDevicesAsync(default!).ReturnsForAnyArgs(true);

        await f.Devices.ToolbarContext.OpenAsync(Oadm.Sdk.Client.HostPages.AddManually);

        await api.DidNotReceiveWithAnyArgs().ListCredentialsAsync(default);
        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowCredentialChoiceAsync(default!);
        await f.Dialogs.Received(1).ShowAddDevicesAsync(Arg.Is<AddDevicesViewModel>(p => p.CredentialIds.Count == 0));
    }

    [Fact]
    public Task The_chosen_ids_reach_every_session_start_of_the_add_page() => _ui.RunAsync(async () =>
    {
        IReadOnlyList<CredentialEntry> entries = await EntriesAsync();
        string[] chosen = [entries[2].Id, entries[4].Id];
        AddDevicesViewModel Page(AddDevicesMode mode) =>
            new(_fake, _ui, NullLogger<AddDevicesViewModel>.Instance, mode) { CredentialIds = chosen };

        await using (AddDevicesViewModel scan = Page(AddDevicesMode.Scan))
        {
            await scan.OpenAsync();
            Assert.Equal(chosen, _fake.LastCredentialIds);
        }

        await using (AddDevicesViewModel range = Page(AddDevicesMode.IpRange))
        {
            _ = await _fake.StartZeroConfAsync([], CancellationToken.None); // forget the last choice
            range.RangeFrom = "10.0.1.20";
            range.RangeTo = "10.0.1.30";
            await range.StartRangeCommand.ExecuteAsync(null);
            Assert.Equal(chosen, _fake.LastCredentialIds);
        }

        await using (AddDevicesViewModel manual = Page(AddDevicesMode.Manual))
        {
            _ = await _fake.StartZeroConfAsync([], CancellationToken.None);
            manual.ManualAddress = "10.0.0.120";
            await manual.ProbeAddressCommand.ExecuteAsync(null);
            Assert.Equal(chosen, _fake.LastCredentialIds);
        }

        await using (AddDevicesViewModel import = Page(AddDevicesMode.Import))
        {
            _ = await _fake.StartZeroConfAsync([], CancellationToken.None);
            import.SetImport(DeviceImportFile.Parse("site.csv", "Address\n10.0.0.120"));
            await import.OpenAsync();
            await TestSupport.WaitUntilAsync(() => _fake.LastCredentialIds.SequenceEqual(chosen));
        }
    });

    [Fact]
    public async Task The_fake_server_refuses_more_than_4_like_the_server()
    {
        string[] five = [.. (await EntriesAsync()).Select(e => e.Id)];

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() => _fake.StartZeroConfAsync(five, CancellationToken.None));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("Choose at most 4 credentials.", ex.Status.Detail);
    }

    // ------------------------------------------------------------ headless

    [Fact]
    public async Task The_dialog_renders_the_list_with_boxes_eyes_and_the_add_editor()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        IReadOnlyList<CredentialEntry> entries = await EntriesAsync();
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var vm = new CredentialChoiceViewModel(_fake, entries, []);
            var window = new CredentialChoiceWindow();
            window.Attach(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            List<CheckBox> boxes = [.. window.GetVisualDescendants().OfType<CheckBox>()];
            Assert.Equal(5, boxes.Count);
            Assert.Equal(4, boxes.Count(b => b.IsChecked == true));
            Assert.False(boxes.Single(b => b.IsChecked != true).IsEffectivelyEnabled); // 4 ticked: the fifth is off
            Assert.Empty(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.PasswordRevealButton>()); // one toggle for all
            Button cont = window.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Continue");
            Assert.Contains("primary", cont.Classes);

            await vm.TogglePasswordsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Fake-root-pass1", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""));
            Capture(window, outDir, "client-add-choose-credentials.png");

            vm.StartAddCommand.Execute(null);
            vm.NewPassword = "";
            vm.FieldLeft(nameof(CredentialChoiceViewModel.NewPassword));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("secondary", cont.Classes); // the editor's Add is the one accent button now
            Assert.Contains("Enter the password.", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""));
            Capture(window, outDir, "client-add-choose-credentials-add.png");

            vm.CancelAddCommand.Execute(null);
            foreach (CredentialChoiceItem item in vm.Items)
            {
                item.IsChecked = false;
            }

            Dispatcher.UIThread.RunJobs();
            Assert.False(cont.IsEffectivelyEnabled);
            Assert.Contains(CredentialChoiceViewModel.NoneChosenText, window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""));
            Capture(window, outDir, "client-add-choose-credentials-none.png");

            window.Close();
            Assert.False(vm.Items[0].Entry.IsRevealed); // forgotten with the dialog
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
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
