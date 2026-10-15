using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>What a limited role sees: the Devices page, the tasks pane, "Your role" and the upgrade notice.</summary>
public sealed class RolePermissionTests
{
    private static UserSession Session(params string[] permissions)
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "guard", Role = UserRole.Operator, RoleId = FakeOadmApi.ViewerRoleId, RoleName = "Viewer", Permissions = { permissions } }, "fake");
        return session;
    }

    [Fact]
    public async Task A_viewer_gets_no_device_actions_and_the_menu_follows_role_changes()
    {
        var session = Session("devices.liveView");
        using var f = new DevicesFixture(session: session);
        f.SeedDevices(TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "P3265-V", DeviceStatus.CredentialsRequired));
        await f.SetPluginsAsync([]); // the role has no tasks: the list arrives empty
        f.Select("d1");
        f.Devices.RebuildContextMenu();
        Assert.Empty(f.Devices.ContextMenuEntries); // no web interface, refresh, log in, tags or remove
        Assert.True(f.Devices.LiveView.IsAllowed);
        var tasks = new Oadm.Client.Tasks.TasksViewModel(f.Tasks, f.Store, f.Api, f.Dialogs, f.Settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Tasks.TasksViewModel>.Instance, session);
        Assert.False(tasks.MayCancel);
        Assert.False(tasks.MayDeleteAll);

        session.Update(new UserInfo { UserName = "guard", Role = UserRole.Operator, RoleId = FakeOadmApi.ViewerRoleId, RoleName = "Viewer", Permissions = { "devices.refresh", "devices.login", "tags.assign" } });
        Assert.Equal(["Refresh", "Log in", "Tags"], f.Devices.ContextMenuEntries.Select(e => e.Header));
        Assert.False(f.Devices.LiveView.IsAllowed);
        Assert.False(f.Devices.LiveView.ToggleCommand.CanExecute(f.Store.Find("d1")));
    }

    [Fact]
    public async Task The_toolbar_hides_entries_the_role_lacks()
    {
        // Controls belong to the headless UI thread: created anywhere else they take over the dispatcher of other tests.
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var session = Session("devices.refresh");
            using var f = new DevicesFixture(session: session);
            f.Devices.Toolbar.ApplyPermissions(session.Has);
            var panel = new StackPanel();
            f.Devices.Toolbar.AttachTo(panel, f.Devices.ToolbarContext);
            Control Entry(string id) => panel.Children.Single(c => c.Name == id);
            Assert.False(Entry("AddButton").IsVisible);
            Assert.False(Entry("oadm.toolbar.remove").IsVisible);
            Assert.False(Entry("ExportButton").IsVisible);
            Assert.True(Entry("oadm.toolbar.refresh").IsVisible);
            Assert.True(Entry("oadm.toolbar.release-notes").IsVisible);
            Assert.True(f.Devices.ToolbarContext.Has("devices.refresh"));
            Assert.False(f.Devices.ToolbarContext.Has("devices.addRemove"));
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Your_role_lists_the_permissions_by_group()
    {
        var api = Substitute.For<IOadmApi>();
        var mine = new MyRole { RoleName = "Viewer" };
        mine.Permissions.Add(new PermissionInfo { Key = "devices.liveView", Name = "Live view", Group = "Devices" });
        mine.Permissions.Add(new PermissionInfo { Key = "oadm.web-view:open", Name = "Open devices", Group = "Web View", FullDeviceControl = true });
        api.GetMyRoleAsync(Arg.Any<CancellationToken>()).Returns(mine);
        var vm = new MyRoleViewModel(api);
        await vm.LoadAsync();
        Assert.Equal("Viewer may:", vm.Intro);
        Assert.Equal(["Devices", "Live view", "Web View", "Open devices"], vm.Rows.Select(r => r.Title));
        Assert.True(vm.Rows[3].FullDeviceControl);

        var admin = new MyRoleViewModel(new FakeOadmApi(TimeSpan.Zero));
        await admin.LoadAsync();
        Assert.Equal("Administrator has every permission, also those that plugins add later.", admin.Intro);
        Assert.Empty(admin.Rows);

        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var window = new MyRoleWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Open devices", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-your-role.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void The_upgrade_notice_shows_once_per_id_until_dismissed()
    {
        var api = Substitute.For<IOadmApi>();
        using var f = new DevicesFixture(api);
        var connection = new ServerConnection(api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
        var admin = new UserSession();
        admin.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }, "fake");
        MainWindowViewModel shell = f.CreateShell(connection, admin);

        var notice = new RolesUpgradeNotice { Id = "n1", OperatorLost = { "Remove users on devices", "Web View: Open devices" } };
        shell.ApplyRolesNotice(notice);
        Assert.Equal("With roles, operators no longer have: Remove users on devices, Web View: Open devices.", shell.RolesNoticeText);
        shell.DismissRolesNoticeCommand.Execute(null);
        Assert.False(shell.HasRolesNotice);
        shell.ApplyRolesNotice(notice);
        Assert.False(shell.HasRolesNotice); // dismissed on this client
        shell.ApplyRolesNotice(new RolesUpgradeNotice { Id = "n2", OperatorLost = { "Downgrade firmware" } });
        Assert.True(shell.HasRolesNotice);
    }
}
