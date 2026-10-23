using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Compliance;
using Oadm.Client.Devices;
using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Dialogs;
using Oadm.Client.EventRules;
using Oadm.Client.Infrastructure;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Client.Sites;
using Oadm.Client.Tags;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Tests;

/// <summary>
/// What users with a device group see beyond the editors (docs/spec/device-scope.md): no event rules page, Add disabled
/// with the server's refusal for a group without a site, the owner of compliance checks for administrators, the questions
/// before deleting a tag or removing a site a group uses, the tag guard's refusal, and fake mode as a group user. Set
/// OADM_SCREENSHOT_DIR for screenshots.
/// </summary>
public sealed class DeviceGroupActionsTests
{
    private static UserSession Admin()
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true, AllDevices = true }, "fake");
        return session;
    }

    private static UserInfo GroupUserInfo(bool allDevices = false) => new()
    {
        UserName = "tech2", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, RoleName = "Operator",
        Permissions = { "devices.addRemove", "events.view", "events.manage", "compliance.view", "compliance.manage", "tags.assign" },
        AllDevices = allDevices, DeviceGroupId = allDevices ? "" : "g1", DeviceGroupName = allDevices ? "" : "Lobby",
    };

    private static UserSession GroupUser()
    {
        var session = new UserSession();
        session.SignIn(GroupUserInfo(), "fake");
        return session;
    }

    private static MainWindowViewModel Shell(DevicesFixture f, ServerConnection connection, UserSession session) => new(
        connection,
        f.Devices,
        new Oadm.Client.Logging.LogsViewModel(new Oadm.Client.Logging.LogStore(f.Ui)),
        new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance, session),
        new UsersViewModel(f.Api, f.Dialogs, session, NullLogger<UsersViewModel>.Instance),
        new CredentialsViewModel(f.Api, connection, f.Clipboard, session),
        new AboutViewModel(f.Api, connection),
        f.Catalog,
        f.Registry,
        f.Api,
        f.Settings,
        session,
        NullLogger<MainWindowViewModel>.Instance,
        eventRules: new EventRulesViewModel(f.Api, f.Dialogs, f.Store, f.Ui, session));

    [Fact]
    public void Group_users_do_not_get_the_event_rules_page()
    {
        UserSession session = GroupUser();
        using var f = new DevicesFixture(session: session);
        var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel shell = Shell(f, connection, session);
        Assert.True(session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.EventsView));
        Assert.DoesNotContain(shell.NavItems, n => n.Key == Oadm.Sdk.Client.HostPages.EventRules);

        // The same role with all devices gets the page back, live; a group again takes it away.
        session.Update(GroupUserInfo(allDevices: true));
        Assert.Contains(shell.NavItems, n => n.Key == Oadm.Sdk.Client.HostPages.EventRules);
        session.Update(GroupUserInfo());
        Assert.DoesNotContain(shell.NavItems, n => n.Key == Oadm.Sdk.Client.HostPages.EventRules);

        UserSession admin = Admin();
        using var g = new DevicesFixture(session: admin);
        MainWindowViewModel adminShell = Shell(g, new ServerConnection(g.Api, g.Store, g.Tasks, g.Ui, NullLogger<ServerConnection>.Instance), admin);
        Assert.Contains(adminShell.NavItems, n => n.Key == Oadm.Sdk.Client.HostPages.EventRules);
    }

    [Fact]
    public async Task Your_role_decides_whether_a_group_user_may_add_devices()
    {
        UserSession session = GroupUser();
        using var f = new DevicesFixture(session: session);
        var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        MainWindowViewModel shell = Shell(f, connection, session);
        var mine = new MyRole { RoleName = "Operator", Devices = new MyDevices { GroupId = "g1", GroupName = "Lobby", SingleDeviceCount = 2, DeviceCount = 2 } };
        f.Api.GetMyRoleAsync(Arg.Any<CancellationToken>()).Returns(mine);

        await shell.RefreshMyDevicesAsync();
        Assert.Equal(DeviceGroupMessages.CannotAdd, session.AddDevicesRefusal);
        Assert.Equal("Your device group has no site, so you cannot add devices.", f.Devices.ToolbarContext.AddDevicesRefusal);

        // A site (or This server) in the group: adding works.
        mine.Devices = new MyDevices { GroupId = "g1", GroupName = "Building B", SiteNames = { "Building B" } };
        await shell.RefreshMyDevicesAsync();
        Assert.Null(session.AddDevicesRefusal);
        mine.Devices = new MyDevices { GroupId = "g1", GroupName = "Head office", ThisServer = true };
        await shell.RefreshMyDevicesAsync();
        Assert.Null(session.AddDevicesRefusal);

        // All devices never asks the server and never refuses; a server before device groups sends no devices.
        mine.Devices = new MyDevices { GroupId = "g1", GroupName = "Lobby" };
        await shell.RefreshMyDevicesAsync();
        Assert.NotNull(session.AddDevicesRefusal);
        f.Api.ClearReceivedCalls();
        session.Update(GroupUserInfo(allDevices: true));
        Assert.Null(session.AddDevicesRefusal);
        await shell.RefreshMyDevicesAsync();
        await f.Api.DidNotReceive().GetMyRoleAsync(Arg.Any<CancellationToken>());
        session.ApplyDevices(null);
        Assert.Null(session.AddDevicesRefusal);
    }

    [Fact]
    public async Task The_add_buttons_are_disabled_with_the_refusal_as_tooltip()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            UserSession session = GroupUser();
            using var f = new DevicesFixture(session: session);
            f.SeedDevices(); // no devices yet: the empty page with its own Add button
            var view = new DevicesView { DataContext = f.Devices };
            var window = new Window { Width = 1400, Height = 700, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ToolbarButton add = window.GetVisualDescendants().OfType<ToolbarButton>().Single(b => b.Name == "AddButton");
            Button empty = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EmptyAddButton");
            Assert.True(add.IsEnabled);
            Assert.Equal(AddToolbarPlugin.Tooltip, ToolTip.GetTip(add));

            session.ApplyDevices(new MyDevices { GroupId = "g1", GroupName = "Lobby", SingleDeviceCount = 2 });
            Dispatcher.UIThread.RunJobs();
            Assert.False(add.IsEnabled);
            Assert.Equal(DeviceGroupMessages.CannotAdd, ToolTip.GetTip(add));
            Assert.True(ToolTip.GetShowOnDisabled(add));
            Assert.False(empty.IsEnabled);
            Assert.Equal(DeviceGroupMessages.CannotAdd, ToolTip.GetTip(empty));
            Capture(window, outDir, "client-group-add-disabled.png");

            session.ApplyDevices(new MyDevices { GroupId = "g1", GroupName = "Building B", SiteNames = { "Building B" } });
            Dispatcher.UIThread.RunJobs();
            Assert.True(add.IsEnabled);
            Assert.Equal(AddToolbarPlugin.Tooltip, ToolTip.GetTip(add));
            Assert.True(empty.IsEnabled);
            Assert.Null(ToolTip.GetTip(empty));
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Administrators_choose_the_owner_of_a_compliance_check()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        IReadOnlyList<Device> devices = await api.ListDevicesAsync(CancellationToken.None);
        DeviceGroupList groups = await api.ListDeviceGroupsAsync(CancellationToken.None);
        ComplianceCatalog catalog = await api.GetComplianceCatalogAsync(CancellationToken.None);
        var store = new DeviceStore();
        store.Reset(devices);
        IReadOnlyList<DeviceAccessOption> owners = [DeviceAccessOption.All, .. groups.Groups.Select(g => new DeviceAccessOption(g.Id, g.Name))];
        DeviceGroupInfo buildingB = groups.Groups.Single(g => g.Name == "Building B");
        var dialogs = Substitute.For<IDialogService>();

        // No owner field without the choices (users who are not administrators).
        var plain = new ComplianceCheckEditorViewModel(api, dialogs, catalog, [.. store.Devices.Cast<IDeviceInfo>()]);
        Assert.False(plain.ShowOwner);

        // A new check: All devices first; the owner goes with the save.
        var editor = new ComplianceCheckEditorViewModel(api, dialogs, catalog, [.. store.Devices.Cast<IDeviceInfo>()], ownerChoices: owners);
        Assert.True(editor.ShowOwner);
        Assert.Same(DeviceAccessOption.All, editor.Owner);
        editor.SelectedType = editor.Types.Single(t => t.Value is ComplianceTypeEntry { Id: "oadm.compliance.reachable" });
        editor.Devices.SetChoice(DeviceChoice.AllDevices);
        editor.Owner = owners.Single(o => o.Title == "Building B");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.ServerError);
        Assert.NotNull(editor.Saved);
        Assert.Equal(buildingB.Id, editor.Saved!.Owner.GroupId);
        Assert.Equal("Building B", editor.Saved.Owner.GroupName);

        // The card names the group for users with all devices only.
        var card = new ComplianceCardViewModel(editor.Saved, showOwner: true);
        Assert.EndsWith(" · Building B", card.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Building B", new ComplianceCardViewModel(editor.Saved).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(" · ", new ComplianceCardViewModel(new ComplianceCheckSummary { Name = "x", Owner = new DeviceOwner { AllDevices = true } }, showOwner: true).Detail, StringComparison.Ordinal);

        // The group is deleted: the check has no owner and is off; the editor asks for one below the field.
        await api.DeleteDeviceGroupAsync(buildingB.Id, CancellationToken.None);
        ComplianceCheckDetail orphan = await api.GetComplianceCheckAsync(editor.Saved.Id, CancellationToken.None);
        Assert.False(orphan.Summary.Enabled);
        Assert.Equal("No devices", new ComplianceCardViewModel(orphan.Summary, showOwner: true).OwnerText);
        IReadOnlyList<DeviceAccessOption> left = [DeviceAccessOption.All, .. (await api.ListDeviceGroupsAsync(CancellationToken.None)).Groups.Select(g => new DeviceAccessOption(g.Id, g.Name))];
        var again = new ComplianceCheckEditorViewModel(api, dialogs, catalog, [.. store.Devices.Cast<IDeviceInfo>()], orphan, ownerChoices: left);
        Assert.Null(again.Owner);
        Assert.Equal(DeviceGroupMessages.ChooseDevices, again.SaveBlockedReason);
        Assert.False(again.SaveCommand.CanExecute(null));
        again.Owner = DeviceAccessOption.All;
        Assert.Null(again.SaveBlockedReason);
    }

    [Fact]
    public async Task Turning_on_a_check_without_an_owner_shows_the_refusal_where_save_errors_show()
    {
        var api = Substitute.For<IOadmApi>();
        api.SaveComplianceCheckAsync(Arg.Any<ComplianceCheckDetail>(), Arg.Any<CancellationToken>())
            .Returns<ComplianceCheckSummary>(_ => throw new RpcException(new Status(StatusCode.FailedPrecondition, DeviceGroupMessages.ChooseDevices)));
        using var fake = new FakeOadmApi(TimeSpan.Zero);
        ComplianceCatalog catalog = await fake.GetComplianceCatalogAsync(CancellationToken.None);
        var existing = new ComplianceCheckDetail
        {
            Summary = new ComplianceCheckSummary
            {
                Id = "c1", Name = "Reachable", TypeId = "oadm.compliance.reachable", Enabled = true, Version = 3, Owner = new DeviceOwner(),
                Devices = new ComplianceDevices { All = true },
            },
        };

        // A user who is not an administrator has no Owner field: the server's refusal stands below the form.
        var editor = new ComplianceCheckEditorViewModel(api, Substitute.For<IDialogService>(), catalog, [], existing);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(DeviceGroupMessages.ChooseDevices, editor.ServerError);
        Assert.Null(editor.Saved);
    }

    [Fact]
    public async Task Screenshot_of_the_check_dialog_with_the_owner()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.Zero);
            var store = new DeviceStore();
            store.Reset(await api.ListDevicesAsync(CancellationToken.None));
            DeviceGroupList groups = await api.ListDeviceGroupsAsync(CancellationToken.None);
            IReadOnlyList<DeviceAccessOption> owners = [DeviceAccessOption.All, .. groups.Groups.Select(g => new DeviceAccessOption(g.Id, g.Name))];
            ComplianceCatalog catalog = await api.GetComplianceCatalogAsync(CancellationToken.None);
            var orphan = new ComplianceCheckDetail
            {
                Summary = new ComplianceCheckSummary { Id = "c1", Name = "Reachable", TypeId = "oadm.compliance.reachable", Enabled = true, Owner = new DeviceOwner(), Devices = new ComplianceDevices { All = true } },
            };
            var editor = new ComplianceCheckEditorViewModel(api, Substitute.For<IDialogService>(), catalog, [.. store.Devices.Cast<IDeviceInfo>()], orphan, ownerChoices: owners);
            await editor.SaveCommand.ExecuteAsync(null); // disabled: shows every error, sends nothing
            var dialog = new ComplianceCheckWindow();
            dialog.Attach(editor);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            List<string> texts = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("Owner", texts);
            Assert.Contains(DeviceGroupMessages.ChooseDevices, texts);
            Capture(dialog, outDir, "client-compliance-owner-missing.png");

            editor.Owner = owners.Single(o => o.Title == "Building B");
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(DeviceGroupMessages.ChooseDevices, dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Capture(dialog, outDir, "client-compliance-owner.png");
            dialog.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Deleting_a_tag_a_group_uses_names_the_group_and_tagging_shows_the_guard()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            await api.ListDeviceGroupsAsync(CancellationToken.None); // the sample groups: Building B uses PTZ, Building A the tag Building A
            using var fixture = new DevicesFixture(api, session: Admin());
            fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            fixture.Store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
            Assert.Equal(["Building B"], fixture.Store.Tags.Find("PTZ")!.GroupNames);
            Assert.Empty(fixture.Store.Tags.Find("Outdoor")!.GroupNames);

            string? question = null;
            var dialog = new DeviceTagsViewModel(api, fixture.Store.Tags, fixture.Store.Devices.Take(1).ToList(), fixture.Store.Devices, isAdmin: true,
                (_, text, _) => { question = text; return Task.FromResult(false); });
            await dialog.DeleteCommand.ExecuteAsync(dialog.Find("PTZ"));
            Assert.NotNull(question);
            Assert.EndsWith("? Used by device group Building B.", question, StringComparison.Ordinal);
            await dialog.DeleteCommand.ExecuteAsync(dialog.Find("Outdoor"));
            Assert.DoesNotContain("Used by", question, StringComparison.Ordinal);

            // A group user tags a device of the group with PTZ: the server refuses, its text stands below the list.
            api.CurrentUserName = "tech2";
            UserSession tech2 = new();
            tech2.SignIn(api.CurrentUser(), "fake");
            var groupFixture = new DevicesFixture(api, session: tech2); // the api goes with the first fixture
            groupFixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            groupFixture.Store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
            Assert.Empty(groupFixture.Store.Tags.Find("PTZ")!.GroupNames); // only users with all devices get the names
            DeviceRowViewModel withoutPtz = groupFixture.Store.Devices.First(d => !d.TagsText.Contains("PTZ", StringComparison.Ordinal));
            var tagging = new DeviceTagsViewModel(api, groupFixture.Store.Tags, [withoutPtz], groupFixture.Store.Devices, isAdmin: false, (_, _, _) => Task.FromResult(true));
            tagging.Find("PTZ")!.IsChecked = true;
            await tagging.ApplyCommand.ExecuteAsync(null);
            Assert.Equal("A device group uses the tag PTZ. Only users with all devices can add or remove it. Nothing was changed.", tagging.ErrorText);

            var window = new DeviceTagsWindow();
            window.Attach(tagging);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(tagging.ErrorText, window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Capture(window, outDir, "client-tags-group-guard.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void Removing_a_site_a_group_uses_names_the_group()
    {
        var api = Substitute.For<IOadmApi>();
        var site = new Site { Id = "s1", Name = "Building B", State = SiteState.Online, DeviceCount = 3, GroupNames = { "Building B" } };
        Assert.Equal("Remove site Building B? It has 3 devices. Used by device group Building B.", SiteFormViewModel.Remove(api, site).Intro);
        site.GroupNames.Add("Night shift");
        Assert.Equal("Remove site Building B? It has 3 devices. Used by device groups Building B, Night shift.", SiteFormViewModel.Remove(api, site).Intro);
        Assert.Equal("Remove site Lobby? It has 1 device.", SiteFormViewModel.Remove(api, new Site { Id = "s2", Name = "Lobby", State = SiteState.Online, DeviceCount = 1 }).Intro);
    }

    [Fact]
    public async Task Fake_mode_runs_as_a_group_user()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        int all = (await api.ListDevicesAsync(CancellationToken.None)).Count;
        SiteList adminSites = await api.ListSitesAsync(CancellationToken.None);
        Assert.Contains(adminSites.Sites, s => s.GroupNames.Contains("Building B"));

        api.CurrentUserName = "tech2";
        UserInfo me = api.CurrentUser();
        Assert.False(me.AllDevices);
        Assert.Equal("Building B", me.DeviceGroupName);
        Assert.False(me.AllPermissions);
        Assert.Contains("devices.addRemove", me.Permissions);
        Assert.Equal("tech2", (await api.GetCurrentUserAsync(CancellationToken.None)).UserName);

        IReadOnlyList<Device> mine = await api.ListDevicesAsync(CancellationToken.None);
        Assert.InRange(mine.Count, 1, all - 1);
        var watched = new List<Device>();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await foreach (DeviceChanged change in api.WatchDevicesAsync(cts.Token))
            {
                if (change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
                {
                    break;
                }

                watched.Add(change.Device);
            }
        }

        Assert.Equal(mine.Select(d => d.Id).Order(), watched.Select(d => d.Id).Order());
        MyRole role = await api.GetMyRoleAsync(CancellationToken.None);
        Assert.Equal("Operator", role.RoleName);
        Assert.Equal("Building B", role.Devices.GroupName);
        Assert.Equal(["Building B"], role.Devices.SiteNames);
        Assert.Equal(mine.Count, role.Devices.DeviceCount);
        SiteList sites = await api.ListSitesAsync(CancellationToken.None);
        Assert.Equal(["Building B"], sites.Sites.Select(s => s.Name));
        Assert.All(sites.Sites, s => Assert.Empty(s.GroupNames));
        Assert.NotNull(await api.StartZeroConfAsync(CancellationToken.None)); // a group with a site adds devices

        // A device outside the group: the web interface answers like a removed one.
        Device outside = (await AllDevicesAsync()).First(d => mine.All(m => m.Id != d.Id));
        var refused = await Assert.ThrowsAsync<RpcException>(() => api.OpenDeviceWebAsync(outside.Id, CancellationToken.None));
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Status.Detail);

        // guard has Lobby: single devices only, so adding is refused with the server's text.
        api.CurrentUserName = "guard";
        var cannot = await Assert.ThrowsAsync<RpcException>(() => api.StartZeroConfAsync(CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, cannot.StatusCode);
        Assert.Equal(DeviceGroupMessages.CannotAdd, cannot.Status.Detail);
        Assert.Empty((await api.GetMyRoleAsync(CancellationToken.None)).Devices.SiteNames);
        Assert.Throws<ArgumentException>(() => api.CurrentUserName = "nobody");

        async Task<IReadOnlyList<Device>> AllDevicesAsync()
        {
            string current = api.CurrentUserName;
            api.CurrentUserName = FakeOadmApi.FakeUserName;
            IReadOnlyList<Device> list = await api.ListDevicesAsync(CancellationToken.None);
            api.CurrentUserName = current;
            return list;
        }
    }

    [Fact]
    public async Task The_fake_web_interface_answers_like_the_server_for_an_unknown_device()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var refused = await Assert.ThrowsAsync<RpcException>(() => api.OpenDeviceWebAsync(Guid.NewGuid().ToString(), CancellationToken.None));
        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Equal("1 device is no longer available.", refused.Status.Detail);
    }

    [Fact]
    public void The_fake_user_comes_from_the_command_line_or_the_environment()
    {
        Assert.Null(AppOptions.Parse(["--fake"], _ => null).FakeUser);
        Assert.Equal("tech2", AppOptions.Parse(["--fake", "--fake-user", "tech2"], _ => null).FakeUser);
        Assert.Equal("guard", AppOptions.Parse(["--fake"], name => name == AppOptions.FakeUserVariable ? " guard " : null).FakeUser);
        Assert.Equal("tech2", AppOptions.Parse(["--fake-user", "tech2"], _ => "guard").FakeUser); // the arguments win
    }

    [Fact]
    public void The_shared_group_texts_are_one_phrase_in_client_and_server()
    {
        Assert.Equal(DeviceGroupMessages.CannotAdd, DeviceGroupWords.CannotAdd);
        Assert.Equal(DeviceGroupMessages.ChooseDevices, DeviceGroupWords.ChooseDevices);
        Assert.Equal("Used by device group Building B", DeviceGroupWords.UsedBy(["Building B"]));
        Assert.Equal("Used by device groups A, B", DeviceGroupMessages.UsedBy(["A", "B"]));
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
