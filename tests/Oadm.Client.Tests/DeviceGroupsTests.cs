using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Logging;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Device groups in the client (docs/spec/device-scope.md "The editors"): the group mode of the shared device picker, the
/// Device groups card and the user form of the Users page, Your role, and what users with a group do not see. Set
/// OADM_SCREENSHOT_DIR for screenshots.
/// </summary>
public sealed class DeviceGroupsTests
{
    private static readonly Guid SiteB = Guid.Parse("5b1e0c3a-7d42-4f6e-9a10-2c8d4e6f0b11");
    private static readonly Guid SiteW = Guid.Parse("9e2d4f60-1a3b-4c5d-8e7f-0a1b2c3d4e22");

    private static readonly DeviceGroupPickerOptions Options = new()
    {
        Sites = [new DevicePickerSite(null, "This server"), new DevicePickerSite(SiteB, "Building B"), new DevicePickerSite(SiteW, "Warehouse")],
        Tags = [new DevicePickerTag("tag-ptz", "PTZ"), new DevicePickerTag("tag-out", "Outdoor")],
    };

    /// <summary>Six devices: two on the server's network (one PTZ), three at Building B, one at Warehouse (outdoor).</summary>
    private static List<FakeDevice> Devices() =>
    [
        new() { Serial = "ACCC8E000001", Address = "10.0.0.1" },
        new() { Serial = "ACCC8E000002", Address = "10.0.0.2", Tags = ["PTZ"] },
        new() { Serial = "ACCC8E000003", Address = "192.168.0.3", SiteId = SiteB, SiteName = "Building B" },
        new() { Serial = "ACCC8E000004", Address = "192.168.0.4", SiteId = SiteB, SiteName = "Building B" },
        new() { Serial = "ACCC8E000005", Address = "192.168.0.5", SiteId = SiteB, SiteName = "Building B", Tags = ["PTZ"] },
        new() { Serial = "ACCC8E000006", Address = "192.168.5.6", SiteId = SiteW, SiteName = "Warehouse", Tags = ["Outdoor"] },
    ];

    private static UserSession Admin()
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true, AllDevices = true }, "fake");
        return session;
    }

    private static UserSession GroupUser(params string[] permissions)
    {
        var session = new UserSession();
        session.SignIn(new UserInfo
        {
            UserName = "tech2", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, RoleName = "Operator",
            Permissions = { permissions }, AllDevices = false, DeviceGroupId = "g1", DeviceGroupName = "Building B",
        }, "fake");
        return session;
    }

    [Fact]
    public void The_group_editor_locks_the_devices_of_sites_and_tags_and_counts_live()
    {
        var picker = new DevicePickerViewModel(Devices(), DeviceGroupChoice.Empty, Options);
        Assert.True(picker.IsGroup);
        Assert.Equal(["This server", "Building B", "Warehouse"], picker.Sites.Select(s => s.Name));
        Assert.Equal(["2", "3", "1"], picker.Sites.Select(s => s.CountText));
        Assert.Equal(["Outdoor", "PTZ"], picker.Tags.Select(t => t.Name)); // by name
        Assert.Equal(["1", "2"], picker.Tags.Select(t => t.CountText));
        Assert.Equal("0 of 6 devices", picker.Summary);

        picker.Sites[1].IsChecked = true; // Building B
        Assert.Equal("3 of 6 devices", picker.Summary);
        DevicePickItem atB = picker.Visible.Single(i => i.Address == "192.168.0.3");
        Assert.True(atB.Chosen);
        Assert.False(atB.CanToggle); // locked
        Assert.Equal("Site Building B", atB.Note);

        picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        DevicePickItem ptz = picker.Visible.Single(i => i.Address == "10.0.0.2");
        Assert.Equal("Tag PTZ", ptz.Note);
        Assert.Equal("4 of 6 devices", picker.Summary);

        // Except: the device stays covered by its site but leaves the group; Include takes it back.
        Assert.True(atB.CanExcept);
        atB.ExceptCommand.Execute(null);
        Assert.False(atB.Chosen);
        Assert.Equal("Excepted from Site Building B", atB.Note);
        Assert.True(atB.CanInclude);
        Assert.Equal("3 of 6 devices", picker.Summary);

        // A single device: ticked by itself.
        DevicePickItem single = picker.Visible.Single(i => i.Address == "10.0.0.1");
        Assert.True(single.CanToggle);
        Assert.False(single.CanExcept);
        single.Chosen = true;
        Assert.Equal("4 of 6 devices", picker.Summary);

        DeviceGroupChoice choice = picker.GroupChoice;
        Assert.False(choice.ThisServer);
        Assert.Equal([SiteB], choice.SiteIds);
        Assert.Equal(["tag-ptz"], choice.TagIds);
        Assert.Equal(["ACCC8E000001"], choice.Serials);
        Assert.Equal(["ACCC8E000003"], choice.ExceptSerials);

        // An except of a device no site or tag covers now is kept (hidden): the device stays out when its site comes back.
        picker.Sites[1].IsChecked = false;
        Assert.Equal(["ACCC8E000003"], picker.GroupChoice.ExceptSerials);
        Assert.False(atB.CanInclude);
        Assert.Null(atB.Note);
        picker.Sites[1].IsChecked = true;
        Assert.Equal(["ACCC8E000003"], picker.GroupChoice.ExceptSerials); // back with its site

        // The search covers the site name.
        picker.SearchText = "warehouse";
        Assert.Equal(["192.168.5.6"], picker.Visible.Select(i => i.Address));
    }

    [Fact]
    public void The_group_editor_opens_with_the_rules_and_keeps_serials_of_devices_not_listed()
    {
        var initial = new DeviceGroupChoice(true, [SiteW], ["tag-ptz"], ["accc8e000004", "B8:A4:4F:00:00:99"], ["ACCC8E000002", "B8A44F000098"]);
        var picker = new DevicePickerViewModel(Devices(), initial, Options);
        Assert.True(picker.Sites[0].IsChecked); // This server
        Assert.True(picker.Sites[2].IsChecked);
        Assert.True(picker.Tags.Single(t => t.Name == "PTZ").IsChecked);
        Assert.Equal("This server", picker.Visible.Single(i => i.Address == "10.0.0.1").Note);
        DevicePickItem excepted = picker.Visible.Single(i => i.Address == "10.0.0.2");
        Assert.Equal("Excepted from This server", excepted.Note);
        Assert.False(excepted.Chosen);
        Assert.True(picker.Visible.Single(i => i.Address == "192.168.0.4").IsChecked);

        // This server: .1 (.2 excepted), Warehouse .6, tag PTZ .5, single .4.
        Assert.Equal("4 of 6 devices", picker.Summary);
        Assert.Equal(4, picker.CoveredCount);
        DeviceGroupChoice choice = picker.GroupChoice;
        Assert.True(choice.ThisServer);
        Assert.Equal(["ACCC8E000004", "B8A44F000099"], choice.Serials);
        Assert.Equal(["ACCC8E000002", "B8A44F000098"], choice.ExceptSerials);
    }

    [Fact]
    public void An_excepted_single_device_is_not_counted_and_ticking_it_takes_it_back()
    {
        // As stored: the device is a single device and excepted (its site was removed from the rules later).
        var initial = new DeviceGroupChoice(false, [], [], ["ACCC8E000001"], ["ACCC8E000001"]);
        var picker = new DevicePickerViewModel(Devices(), initial, Options);
        DevicePickItem single = picker.Visible.Single(i => i.Address == "10.0.0.1");

        Assert.False(single.Chosen); // the server leaves it out: excepted wins
        Assert.Equal(0, picker.CoveredCount);
        Assert.Equal("0 of 6 devices", picker.Summary);

        single.Chosen = true;
        Assert.True(single.Chosen);
        Assert.False(single.IsExcepted);
        Assert.Equal("1 of 6 devices", picker.Summary);
        Assert.Equal(["ACCC8E000001"], picker.GroupChoice.Serials);
        Assert.Empty(picker.GroupChoice.ExceptSerials);
    }

    [Fact]
    public void The_other_picker_modes_stay_as_they_were()
    {
        var picker = new DevicePickerViewModel(Devices(), DeviceChoice.Devices([], ["PTZ"]), DevicePickerOptions.Full());
        Assert.False(picker.IsGroup);
        Assert.Empty(picker.Sites);
        Assert.Equal("Tag PTZ", picker.Visible.Single(i => i.Address == "10.0.0.2").Note);
        Assert.False(picker.Visible.Single(i => i.Address == "10.0.0.2").CanExcept);
        Assert.Equal("1 tag: 2 of 6 devices", picker.Summary);
    }

    [Fact]
    public async Task The_card_lists_the_groups_and_delete_names_the_users_who_lose_their_devices()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var users = new UsersViewModel(api, dialogs, Admin(), NullLogger<UsersViewModel>.Instance);
        await users.LoadAsync();
        DeviceGroupsViewModel groups = users.DeviceGroups;
        Assert.Equal(["Building A", "Building B", "Head office", "Lobby"], groups.Groups.Select(g => g.Name));
        DeviceGroupRowViewModel b = groups.Groups.Single(g => g.Name == "Building B");
        Assert.Equal("Site Building B, tag PTZ, 3 devices, except 1", b.Summary);
        Assert.Equal("tech2", b.UsersText);
        Assert.True(b.DeviceCount > 0);
        Assert.Equal("Building B", users.Users.Single(u => u.UserName == "tech2").DevicesText);
        Assert.Equal("All devices", users.Users.Single(u => u.UserName == "tech1").DevicesText);
        Assert.Equal(["All devices", "Building A", "Building B", "Head office", "Lobby"], users.DeviceOptions.Select(o => o.Title));

        Assert.Equal("tech2 has no devices until you give them another group. Delete Building B?", DeviceGroupsViewModel.DeleteQuestion(b));
        await groups.DeleteCommand.ExecuteAsync(b);
        await dialogs.Received(1).ConfirmAsync("Delete device group", "tech2 has no devices until you give them another group. Delete Building B?", "Delete");
        Assert.DoesNotContain(groups.Groups, g => g.Name == "Building B");
        await TestSupport.WaitUntilAsync(() => users.Users.Single(u => u.UserName == "tech2").DevicesText == "No devices");
        Assert.DoesNotContain(users.DeviceOptions, o => o.Title == "Building B");
    }

    [Fact]
    public async Task The_editor_saves_the_rules_and_shows_refusals_below_the_field()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var dialogs = Substitute.For<IDialogService>();
        var groups = new DeviceGroupsViewModel(api, dialogs, Admin(), () => Devices());
        await groups.LoadAsync();

        DeviceGroupEditorViewModel editor = (await groups.PrepareEditorAsync(null))!;
        Assert.Equal("New device group", editor.Title);
        Assert.Null(editor.ErrorOf(nameof(editor.Name))); // untouched form: no error yet
        Assert.False(editor.CanSave);
        Assert.Equal("Enter a name.", editor.SaveBlockedReason);
        editor.Name = "lobby"; // in use (any case)
        Assert.Equal("A device group named lobby exists already.", editor.ErrorOf(nameof(editor.Name)));
        editor.Name = "Building C";
        Assert.True(editor.CanSave);
        editor.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        editor.Picker.Visible.Single(i => i.Address == "10.0.0.1").Chosen = true;
        Assert.True(await editor.SaveAsync());
        Assert.Equal("Building C", editor.Saved!.Name);
        Assert.Equal("tag PTZ, 1 device", editor.Saved.Summary);

        // Edit: the stored rules come back; a save of an older revision is refused below the device list.
        await groups.LoadAsync();
        DeviceGroupRowViewModel row = groups.Groups.Single(g => g.Name == "Building C");
        DeviceGroupEditorViewModel first = (await groups.PrepareEditorAsync(row))!;
        DeviceGroupEditorViewModel second = (await groups.PrepareEditorAsync(row))!;
        Assert.Equal("Edit Building C", first.Title);
        Assert.True(first.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked);
        Assert.Equal(["ACCC8E000001"], first.Picker.GroupChoice.Serials);
        first.Description = "Every PTZ camera";
        Assert.True(await first.SaveAsync());
        second.Description = "Changed meanwhile";
        Assert.False(await second.SaveAsync());
        Assert.Equal("Another administrator changed this device group meanwhile. Close it and make your changes again.", second.Picker.ErrorText);
        Assert.Null(second.ErrorOf(nameof(second.Name)));

        // A name taken meanwhile: below the name.
        DeviceGroupEditorViewModel third = (await groups.PrepareEditorAsync(null))!;
        await api.SaveDeviceGroupAsync(new SaveDeviceGroupRequest { Name = "Taken" }, CancellationToken.None);
        third.Name = "Taken";
        Assert.False(await third.SaveAsync());
        Assert.Equal("A device group named Taken exists already.", third.ErrorOf(nameof(third.Name)));
    }

    [Fact]
    public async Task Errors_that_are_not_server_refusals_show_in_the_card_and_below_the_device_list()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListSitesAsync(Arg.Any<CancellationToken>()).Returns<SiteList>(_ => throw new HttpRequestException("The connection broke."));
        api.SaveDeviceGroupAsync(Arg.Any<SaveDeviceGroupRequest>(), Arg.Any<CancellationToken>()).Returns<DeviceGroupInfo>(_ => throw new HttpRequestException("The connection broke."));
        var groups = new DeviceGroupsViewModel(api, Substitute.For<IDialogService>(), Admin());

        await groups.NewCommand.ExecuteAsync(null);
        Assert.Equal("The connection broke.", groups.ErrorText);
        Assert.False(groups.IsOpening);

        var editor = new DeviceGroupEditorViewModel(api, [], [], [], null, []) { Name = "Lobby" };
        Assert.False(await editor.SaveAsync());
        Assert.Equal("The connection broke.", editor.Picker.ErrorText);
        Assert.False(editor.IsSaving);
    }

    [Fact]
    public async Task The_user_form_offers_all_devices_or_a_group_and_administrators_always_have_all()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var users = new UsersViewModel(api, dialogs, Admin(), NullLogger<UsersViewModel>.Instance);
        await users.LoadAsync();

        users.OpenAddCommand.Execute(null);
        Assert.Equal(DeviceAccessOption.All, users.NewDevices);
        Assert.Null(users.NewDevicesHint);
        users.NewUserName = "site-b";
        users.NewPassword = "Site-b-pass-1";
        users.NewDevices = users.DeviceOptions.Single(o => o.Title == "Lobby");
        Assert.Equal(DeviceGroupWords.ServerWideNote, users.NewDevicesHint);
        users.NewRole = users.Roles.Single(r => r.Kind == RoleKind.Administrator);
        Assert.False(users.IsNewDevicesEnabled);
        Assert.Equal("Administrators always have all devices.", users.NewDevicesHint);
        users.NewRole = users.Roles.Single(r => r.Kind == RoleKind.Operator);
        await users.SaveFormCommand.ExecuteAsync(null);
        Assert.Equal(UserForm.None, users.Form);
        Assert.Equal("Lobby", users.Users.Single(u => u.UserName == "site-b").DevicesText);

        // Edit: the group; a user whose group was deleted has no choice until one is made.
        UserRowViewModel siteB = users.Users.Single(u => u.UserName == "site-b");
        users.OpenEditCommand.Execute(siteB);
        Assert.Equal("Lobby", users.EditDevices?.Title);
        users.EditDevices = DeviceAccessOption.All;
        await users.SaveFormCommand.ExecuteAsync(null);
        Assert.Equal("All devices", users.Users.Single(u => u.UserName == "site-b").DevicesText);

        users.OpenEditCommand.Execute(users.Users.Single(u => u.UserName == "site-b"));
        users.EditDevices = users.DeviceOptions.Single(o => o.Title == "Head office");
        await users.SaveFormCommand.ExecuteAsync(null);
        await users.DeviceGroups.DeleteCommand.ExecuteAsync(users.DeviceGroups.Groups.Single(g => g.Name == "Head office"));
        await TestSupport.WaitUntilAsync(() => users.Users.Single(u => u.UserName == "site-b").DevicesText == "No devices");
        users.OpenEditCommand.Execute(users.Users.Single(u => u.UserName == "site-b"));
        Assert.Null(users.EditDevices);
        Assert.Equal("Choose All devices or a device group.", users.FormBlockedReason);
        Assert.False(users.SaveFormCommand.CanExecute(null));
        users.CloseForm();
    }

    [Fact]
    public async Task An_administrators_form_shows_the_stored_group_and_a_role_change_sends_the_devices_choice()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var dialogs = Substitute.For<IDialogService>();
        var users = new UsersViewModel(api, dialogs, Admin(), NullLogger<UsersViewModel>.Instance);
        await users.LoadAsync();
        string lobby = users.DeviceOptions.Single(o => o.Title == "Lobby").GroupId!;
        string adminRole = users.Roles.Single(r => r.Kind == RoleKind.Administrator).Id;
        await api.AddUserAsync(new AddUserRequest { UserName = "boss", Password = "Boss-pass-123", RoleId = adminRole, AllDevices = true }, CancellationToken.None);
        string id = (await api.ListUsersAsync(CancellationToken.None)).Single(u => u.UserName == "boss").Id;

        async Task<UserRowViewModel> AdminWithLobbyAsync()
        {
            await api.UpdateUserAsync(new UpdateUserRequest { Id = id, RoleId = adminRole, AllDevices = false, DeviceGroupId = lobby }, CancellationToken.None);
            await users.LoadAsync();
            return users.Users.Single(u => u.UserName == "boss");
        }

        // The administrator has all devices; the form shows the group stored on the account (locked).
        var boss = await AdminWithLobbyAsync();
        Assert.Equal("All devices", boss.DevicesText);
        users.OpenEditCommand.Execute(boss);
        Assert.Equal("Lobby", users.EditDevices?.Title);
        Assert.False(users.IsEditDevicesEnabled);

        // Operator with All devices chosen: All devices.
        users.EditRole = users.Roles.Single(r => r.Kind == RoleKind.Operator);
        Assert.True(users.IsEditDevicesEnabled);
        users.EditDevices = DeviceAccessOption.All;
        await users.SaveFormCommand.ExecuteAsync(null);
        Assert.Equal("All devices", users.Users.Single(u => u.UserName == "boss").DevicesText);

        // Operator with the shown group left as it is: the group is sent with the role, not applied unseen.
        boss = await AdminWithLobbyAsync();
        users.OpenEditCommand.Execute(boss);
        users.EditRole = users.Roles.Single(r => r.Kind == RoleKind.Operator);
        await users.SaveFormCommand.ExecuteAsync(null);
        Assert.Equal(UserForm.None, users.Form);
        UserInfo stored = (await api.ListUsersAsync(CancellationToken.None)).Single(u => u.UserName == "boss");
        Assert.Equal((false, lobby), (stored.AllDevices, stored.DeviceGroupId));
        Assert.Equal("Lobby", users.Users.Single(u => u.UserName == "boss").DevicesText);
    }

    [Fact]
    public async Task Your_role_shows_the_group_and_its_rules()
    {
        var api = Substitute.For<IOadmApi>();
        var mine = new MyRole { RoleName = "Operator", Devices = new MyDevices { GroupId = "g1", GroupName = "Building B", Summary = "Site Building B, tag PTZ, 3 devices", DeviceCount = 412 } };
        api.GetMyRoleAsync(Arg.Any<CancellationToken>()).Returns(mine);
        var vm = new MyRoleViewModel(api);
        await vm.LoadAsync();
        Assert.Equal("Devices: Building B (412 devices)", vm.DevicesText);
        Assert.Equal("Site Building B, tag PTZ, 3 devices", vm.DevicesDetail);

        mine.Devices = new MyDevices(); // the group was deleted
        await vm.LoadAsync();
        Assert.Equal("Devices: No devices", vm.DevicesText);
        Assert.False(vm.HasDevicesDetail);

        mine.Devices = null; // a server before device groups
        await vm.LoadAsync();
        Assert.Equal("Devices: All devices", vm.DevicesText);

        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        mine.Devices = new MyDevices { GroupId = "g1", GroupName = "Building B", Summary = "Site Building B, tag PTZ, 3 devices", DeviceCount = 412 };
        mine.Permissions.Add(new PermissionInfo { Key = "devices.refresh", Name = "Refresh devices", Group = "Devices" });
        await vm.LoadAsync();
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var window = new MyRoleWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Devices: Building B (412 devices)", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Capture(window, outDir, "client-your-role-group.png");
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Users_with_a_group_do_not_get_the_actions_for_all_devices()
    {
        UserSession session = GroupUser("tasks.deleteAll", "events.clearLog", "logs.server", "compliance.manage", "tags.manage");
        Assert.False(session.HasAllDevices);
        Assert.Equal("Building B", session.DeviceGroupName);

        using var f = new DevicesFixture(session: session);
        var tasks = new Oadm.Client.Tasks.TasksViewModel(f.Tasks, f.Store, f.Api, f.Dialogs, f.Settings, NullLogger<Oadm.Client.Tasks.TasksViewModel>.Instance, session);
        Assert.False(tasks.MayDeleteAll);
        var logs = new LogsViewModel(new LogStore(f.Ui), session: session, server: new ServerLogViewModel(f.Api));
        Assert.False(logs.CanSeeServer);
        Assert.False(f.Devices.ToolbarContext.HasAllDevices);
        var context = new Oadm.Client.Plugins.TaskDialogContext(f.Api, "oadm.restart", session.Has, hasAllDevices: () => session.HasAllDevices);
        Assert.False(context.HasAllDevices);

        // The same role with all devices gets them back, live.
        bool raised = false;
        session.PermissionsChanged += (_, _) => raised = true;
        session.Update(new UserInfo
        {
            UserName = "tech2", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, RoleName = "Operator",
            Permissions = { "tasks.deleteAll", "events.clearLog", "logs.server", "compliance.manage", "tags.manage" }, AllDevices = true,
        });
        Assert.True(raised);
        Assert.True(session.HasAllDevices);
        Assert.True(tasks.MayDeleteAll);
        Assert.True(logs.CanSeeServer);
        Assert.True(f.Devices.ToolbarContext.HasAllDevices);

        // A server before device groups sends no all_devices: every device.
        var old = new UserSession();
        old.SignIn(new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId }, "fake");
        Assert.True(old.HasAllDevices);
        await Task.CompletedTask;
    }

    [Fact]
    public void Group_users_cannot_rename_tags_or_turn_compliance_off()
    {
        UserSession session = GroupUser("compliance.manage", "tags.manage", "tags.assign", "events.view", "events.manage", "events.clearLog");
        using var compliance = new Oadm.Client.Compliance.ComplianceViewModel(Substitute.For<IOadmApi>(), Substitute.For<IDialogService>(),
            new Oadm.Client.Devices.DeviceStore(), new Oadm.Client.Infrastructure.ImmediateUiDispatcher(), new Oadm.Client.Infrastructure.InMemoryClientSettingsStore(), session);
        Assert.True(compliance.MayManage);
        Assert.False(compliance.MayTurnOnOff);
    }

    [Fact]
    public async Task The_card_shows_that_it_reads_the_groups()
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = Substitute.For<IOadmApi>();
            var reply = new TaskCompletionSource<DeviceGroupList>();
            api.ListDeviceGroupsAsync(Arg.Any<CancellationToken>()).Returns(reply.Task);
            var groups = new DeviceGroupsViewModel(api, Substitute.For<IDialogService>(), Admin());
            var window = new Window { Width = 800, Height = 400, Content = new DeviceGroupsCardView { DataContext = groups } };
            window.Show();
            Task load = groups.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            ProgressRow row = window.GetVisualDescendants().OfType<ProgressRow>().Single(r => r.Text == "Reading the device groups");
            Assert.True(row.IsVisible);
            Assert.False(groups.IsEmpty); // no "No device groups yet" while reading

            reply.SetResult(new DeviceGroupList());
            await load;
            Dispatcher.UIThread.RunJobs();
            Assert.False(row.IsVisible);
            Assert.True(groups.IsEmpty);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Screenshots_of_the_card_the_editor_and_the_user_form()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var dialogs = Substitute.For<IDialogService>();
            IReadOnlyList<Device> devices = await api.ListDevicesAsync(CancellationToken.None);
            var store = new Oadm.Client.Devices.DeviceStore();
            store.Reset(devices);
            var groups = new DeviceGroupsViewModel(api, dialogs, Admin(), () => [.. store.Devices]);
            var users = new UsersViewModel(api, dialogs, Admin(), NullLogger<UsersViewModel>.Instance, groups: groups);
            await users.LoadAsync();

            // A wide page: users and device groups side by side; a small or scaled screen: the groups below the users.
            var view = new UsersPageView { DataContext = users, Margin = new Avalonia.Thickness(16) };
            var page = new Window { Width = 1900, Height = 1100, Content = view };
            page.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.HasTwoColumns);
            Capture(page, outDir, "client-permissions-wide.png");
            page.Width = 1050; // the page of a 1280 px window with the rail
            page.Height = 640;
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.HasTwoColumns);
            Capture(page, outDir, "client-permissions-1280.png");
            page.Width = 1400;
            page.Height = 1100;
            Dispatcher.UIThread.RunJobs();
            List<string> texts = page.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Device groups", texts);
            Assert.Contains("Site Building B, tag PTZ, 3 devices, except 1", texts);
            // The Devices column: right-aligned like every numeric column (docs/spec/security.md).
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("numberHeader") && t.Text == "Devices");
            Assert.Contains(groups.Groups.Single(g => g.Name == "Building B").DeviceCountText, texts);
            Capture(page, outDir, "client-device-groups-card.png");
            page.Close();

            DeviceGroupEditorViewModel editor = (await groups.PrepareEditorAsync(groups.Groups.Single(g => g.Name == "Building B")))!;
            DevicePickerWindow window = DeviceGroupFormView.CreateWindow(editor);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Edit Building B", window.Heading);
            Assert.Equal("Save", window.AcceptButton.Content);
            texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Sites", texts);
            Assert.Contains("Tags", texts);
            Assert.Contains("This server", texts);
            Assert.Contains(texts, t => t.EndsWith(" devices", StringComparison.Ordinal) && t.Contains(" of ", StringComparison.Ordinal));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Except" && b.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Include" && b.IsEffectivelyVisible);
            Capture(window, outDir, "client-device-group-editor.png");

            editor.Name = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.AcceptButton.IsEnabled);
            Assert.Contains("Enter a name.", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Capture(window, outDir, "client-device-group-editor-error.png");
            window.Close();

            users.OpenAddCommand.Execute(null);
            users.NewUserName = "site-b";
            users.NewDevices = users.DeviceOptions.Single(o => o.Title == "Building B");
            var add = new UserFormWindow();
            add.Attach(users);
            add.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(DeviceGroupWords.ServerWideNote, add.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Capture(add, outDir, "client-user-add-devices.png");
            add.Close();

            users.OpenEditCommand.Execute(users.Users.Single(u => u.UserName == "anna.berg"));
            var edit = new UserFormWindow();
            edit.Attach(users);
            edit.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Administrators always have all devices.", edit.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Capture(edit, outDir, "client-user-edit-admin-devices.png");
            edit.Close();
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
