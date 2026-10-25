using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The permission matrix of the Users page (roles card) and the session's permissions.</summary>
public sealed class RolesMatrixTests
{
    private static (RolesViewModel Vm, FakeOadmApi Api, IDialogService Dialogs) Create(bool confirm = true)
    {
        var api = new FakeOadmApi(TimeSpan.Zero);
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(confirm);
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = FakeOadmApi.FakeUserName, Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }, "fake");
        return (new RolesViewModel(api, dialogs, session), api, dialogs);
    }

    /// <summary>Loads the matrix and opens every group (groups start collapsed).</summary>
    private static async Task LoadOpenAsync(RolesViewModel vm)
    {
        await vm.LoadAsync();
        foreach (var group in vm.Rows.Where(r => r.IsGroup && !r.IsExpanded).ToList())
        {
            vm.ToggleGroupCommand.Execute(group);
        }
    }

    private static PermissionRowViewModel Row(RolesViewModel vm, string key) => vm.Rows.Single(r => r.Key == key);

    private static RoleColumnViewModel Column(RolesViewModel vm, string name) => vm.Columns.Single(c => c.Name == name);

    [Fact]
    public async Task The_matrix_lists_groups_permissions_and_roles()
    {
        var (vm, _, _) = Create();
        await LoadOpenAsync(vm);

        Assert.Equal(["Administrator", "Operator", "Viewer", "Contractor"], vm.Columns.Select(c => c.Name));
        Assert.Equal(["Everything", "2 users", "1 user", "1 user"], vm.Columns.Select(c => c.Subtitle));
        Assert.Equal("Devices", vm.Rows[0].Title);
        Assert.True(vm.Rows[0].IsGroup);
        Assert.Contains(vm.Rows, r => r.IsGroup && r.Title == "Web View");

        // Administrator: every box, none editable. Operator: the group is partly on.
        var devices = vm.Rows[0];
        Assert.True(devices.Cells[0].IsChecked);
        Assert.False(devices.Cells[0].IsEnabled);
        Assert.Null(devices.Cells[1].IsChecked);
        Assert.True(Row(vm, "devices.addRemove").Cells[1].IsChecked);
        Assert.False(Row(vm, "tags.manage").Cells[1].IsChecked);
        Assert.True(Row(vm, "task:oadm.users:remove").FullDeviceControl);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Every_role_has_a_start_page_even_the_administrator_role()
    {
        var (vm, api, _) = Create();
        await LoadOpenAsync(vm);
        Assert.All(vm.Columns, c => Assert.Equal("Starts on Devices", c.StartPageText));

        // The Administrator role: only the start page, name and description stay.
        var admin = Column(vm, "Administrator");
        vm.EditRoleCommand.Execute(admin);
        Assert.Equal("Edit Administrator", vm.EditorTitle);
        Assert.False(vm.IsNameEditable);
        Assert.Equal(["Devices", "Compliance", "Sites", "Event rules"], vm.StartPageChoices.Take(4).Select(c => c.Title));
        Assert.Equal("Logs", vm.StartPageChoices[^1].Title);
        vm.EditStartPage = vm.StartPageChoices.Single(c => c.Key == "compliance");
        vm.ApplyEditorCommand.Execute(null);
        Assert.Equal("Starts on Compliance", admin.StartPageText);
        Assert.True(vm.IsDirty);

        // Operator: a plugin page of the server.
        var op = Column(vm, "Operator");
        vm.EditRoleCommand.Execute(op);
        Assert.True(vm.IsNameEditable);
        var plugin = vm.StartPageChoices.First(c => c.Key.StartsWith("plugin:", StringComparison.Ordinal));
        vm.EditStartPage = plugin;
        vm.ApplyEditorCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirty);
        var matrix = await api.GetRoleMatrixAsync(CancellationToken.None);
        Assert.Equal("compliance", matrix.Roles.Single(r => r.Name == "Administrator").StartPage);
        Assert.Equal(plugin.Key, matrix.Roles.Single(r => r.Name == "Operator").StartPage);
        Assert.Equal("Starts on " + plugin.Title, Column(vm, "Operator").StartPageText);
    }

    [Fact]
    public async Task Checking_a_group_gives_all_and_the_changes_are_saved_together()
    {
        var (vm, api, dialogs) = Create();
        await LoadOpenAsync(vm);
        var viewer = Column(vm, "Viewer");

        var devices = vm.Rows[0];
        devices.Cells[2].IsChecked = true; // some -> all
        Assert.True(devices.Cells[2].IsChecked);
        Assert.True(Row(vm, "devices.addRemove").Cells[2].IsChecked);
        Assert.True(vm.IsDirty);
        Assert.True(viewer.IsDirty);

        devices.Cells[2].IsChecked = false; // all -> none
        Assert.False(Row(vm, "devices.liveView").Cells[2].IsChecked);

        Row(vm, "devices.refresh").Cells[2].IsChecked = true;
        Assert.Null(devices.Cells[2].IsChecked);

        // The Administrator column does not change.
        Row(vm, "devices.refresh").Cells[0].IsChecked = false;
        Assert.True(Row(vm, "devices.refresh").Cells[0].IsChecked);

        await vm.SaveCommand.ExecuteAsync(null);
        await dialogs.Received(1).ConfirmAsync("Save roles", Arg.Is<string>(s => s.Contains("Viewer: +Refresh devices, -Live view", StringComparison.Ordinal)), "Save");
        Assert.False(vm.IsDirty);
        var saved = (await api.GetRoleMatrixAsync(CancellationToken.None)).Roles.Single(r => r.Name == "Viewer");
        Assert.Contains("devices.refresh", saved.Permissions);
        Assert.DoesNotContain("devices.liveView", saved.Permissions);
    }

    [Fact]
    public async Task Discard_reset_and_a_refused_confirmation_keep_the_server_unchanged()
    {
        var (vm, api, _) = Create(confirm: false);
        await LoadOpenAsync(vm);
        Row(vm, "tags.manage").Cells[1].IsChecked = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsDirty); // not confirmed: nothing saved
        Assert.DoesNotContain("tags.manage", (await api.GetRoleMatrixAsync(CancellationToken.None)).Roles.Single(r => r.Name == "Operator").Permissions);

        await vm.DiscardCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirty);
        Assert.False(Row(vm, "tags.manage").Cells[1].IsChecked);

        // Reset to default: Operator gets back what the plugins and OADM give it by default.
        var op = Column(vm, "Operator");
        Row(vm, "devices.addRemove").Cells[1].IsChecked = false;
        Row(vm, "events.manage").Cells[1].IsChecked = true;
        vm.ResetRoleCommand.Execute(op);
        Assert.True(Row(vm, "devices.addRemove").Cells[1].IsChecked);
        Assert.False(Row(vm, "events.manage").Cells[1].IsChecked);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_new_role_or_a_copy_is_a_column_until_saved_and_names_are_unique()
    {
        var (vm, api, _) = Create();
        await LoadOpenAsync(vm);

        vm.NewRoleCommand.Execute(null);
        Assert.True(vm.IsNewEditor);
        vm.EditName = "operator";
        Assert.Equal("A role named operator exists already.", vm.ErrorOf(nameof(vm.EditName)));
        Assert.False(vm.ApplyEditorCommand.CanExecute(null));
        vm.EditName = "Guard";
        vm.StartFrom = Column(vm, "Viewer");
        vm.ApplyEditorCommand.Execute(null);
        var guard = Column(vm, "Guard");
        Assert.True(guard.IsNew);
        Assert.Equal("Not saved", guard.Subtitle);
        Assert.True(Row(vm, "devices.liveView").Cells[4].IsChecked);
        Assert.True(vm.IsDirty);

        // An unsaved role leaves at once.
        await vm.DeleteRoleCommand.ExecuteAsync(guard);
        Assert.DoesNotContain(vm.Columns, c => c.Name == "Guard");
        Assert.False(vm.IsDirty);

        vm.CopyRoleCommand.Execute(Column(vm, "Operator"));
        Assert.Equal("Copy of Operator", vm.EditName);
        vm.ApplyEditorCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        var copy = (await api.GetRoleMatrixAsync(CancellationToken.None)).Roles.Single(r => r.Name == "Copy of Operator");
        Assert.Equal(RoleKind.Custom, copy.Kind);
        Assert.Contains("devices.addRemove", copy.Permissions);

        // A role with users stays; the reason is shown.
        await vm.DeleteRoleCommand.ExecuteAsync(Column(vm, "Contractor"));
        Assert.Contains(vm.Columns, c => c.Name == "Contractor");
    }

    [Fact]
    public async Task Role_menus_are_the_shared_row_menu_with_Delete_red_and_last()
    {
        var (vm, _, _) = Create();
        await vm.LoadAsync();

        Assert.Equal(["Edit", "Copy"], vm.MenuOf(Column(vm, "Administrator")).Select(a => a.Text));
        Assert.DoesNotContain(vm.MenuOf(Column(vm, "Operator")), a => a.IsDanger);

        var custom = vm.MenuOf(Column(vm, "Contractor"));
        Assert.True(custom[^2].IsSeparator);
        Assert.Equal("Delete", custom[^1].Text);
        Assert.True(custom[^1].IsDanger);
        Assert.Single(custom, a => a.IsDanger);
    }

    [Fact]
    public async Task Search_and_collapsed_groups_filter_the_rows()
    {
        var (vm, _, _) = Create();
        await vm.LoadAsync();
        int all = vm.Rows.Count;

        vm.SearchText = "remove";
        Assert.All(vm.Rows.Where(r => !r.IsGroup), r => Assert.Contains("emove", r.Title + r.Description, StringComparison.Ordinal));
        Assert.Contains(vm.Rows, r => r.Key == "task:oadm.users:remove");

        vm.SearchText = "";
        Assert.Equal(all, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.IsGroup)); // groups start collapsed
        vm.ToggleGroupCommand.Execute(vm.Rows[0]);
        Assert.Equal("Devices", vm.Rows[0].Title);
        Assert.False(vm.Rows[1].IsGroup); // the Devices rows are shown
    }

    [Fact]
    public void The_session_follows_role_and_permissions()
    {
        var session = new UserSession();
        int changes = 0;
        session.PermissionsChanged += (_, _) => changes++;
        session.SignIn(new UserInfo { UserName = "tech1", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, RoleName = "Operator", Permissions = { "devices.refresh" } }, "fake");
        Assert.True(session.Has("devices.refresh"));
        Assert.False(session.Has("devices.addRemove"));
        Assert.False(session.IsAdmin);
        Assert.Equal("Operator", session.RoleText);

        session.Update(new UserInfo { UserName = "tech1", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, RoleName = "Operator", Permissions = { "devices.refresh" } });
        Assert.Equal(1, changes); // nothing changed: no event

        session.Update(new UserInfo { UserName = "tech1", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, RoleName = "Administrator", AllPermissions = true });
        Assert.Equal(2, changes);
        Assert.True(session.IsAdmin);
        Assert.True(session.Has("anything"));

        // An older server sends no permissions: everything but the Administrator-only parts, like before roles.
        var legacy = new UserSession();
        legacy.SignIn(new UserInfo { UserName = "tech1", Role = UserRole.Operator }, "old");
        Assert.True(legacy.Has("devices.addRemove"));
        Assert.False(legacy.IsAdmin);
    }

    [Fact]
    public async Task The_matrix_renders_with_plugin_groups_and_warnings()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (vm, _, _) = Create();
            await LoadOpenAsync(vm);
            Row(vm, "task:oadm.users:remove").Cells[1].IsChecked = true;
            vm.ToggleGroupCommand.Execute(vm.Rows.Single(r => r.IsGroup && r.Title == "Devices"));
            var window = new Window { Width = 1300, Height = 760, Content = new RolesCardView { DataContext = vm, Margin = new Avalonia.Thickness(16) } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "client-roles-matrix.png");

            vm.SearchText = "users";
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Remove users on devices", texts);
            Assert.Contains("Contractor", texts);

            Assert.Contains(window.GetVisualDescendants().OfType<Border>(), b => Equals(ToolTip.GetTip(b), RolesCardView.FullDeviceControlTip));

            vm.NewRoleCommand.Execute(null);
            vm.EditName = "Guard";
            vm.SearchText = "";
            var roleWindow = new RoleFormWindow();
            roleWindow.Attach(vm);
            roleWindow.Show();
            Capture(roleWindow, outDir, "client-roles-new.png");
            vm.ApplyEditorCommand.Execute(null);
            Assert.False(roleWindow.IsVisible); // applied: the dialog closed

            // The Administrator role: only its start page.
            vm.EditRoleCommand.Execute(Column(vm, "Administrator"));
            var editWindow = new RoleFormWindow();
            editWindow.Attach(vm);
            editWindow.Show();
            Capture(editWindow, outDir, "client-roles-edit-admin.png");
            Assert.Contains(editWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Start page");
            vm.CancelEditorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, outDir, "client-roles-start-page.png");
            window.Close();
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
