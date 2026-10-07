namespace Oadm.Plugins.Users.Tests;

/// <summary>The dialog lists only accounts in the admin (root), operator or viewer group.</summary>
public sealed class VisibleUsersTests
{
    [Fact]
    public void Accounts_without_an_access_group_are_hidden_from_the_dialog()
    {
        const string body = """
            admin="root,adminuser"
            operator="root,adminuser,op"
            viewer="root,adminuser,op,view"
            ptz="root"
            digusers="root,adminuser,op,view,sshonly,service"
            """;

        var all = PwdgrpApi.ParseUsers(body, "root");
        var visible = UsersTaskPlugin.VisibleUsers(all);

        // The full list still knows every account (safety checks need it) ...
        Assert.Contains(all, u => u.Name == "sshonly");
        Assert.Contains(all, u => u.Name == "service");

        // ... but the dialog shows only Administrator, Operator and Viewer accounts.
        Assert.Equal(["root", "adminuser", "op", "view"], visible.Select(u => u.Name).ToArray());
        Assert.DoesNotContain(visible, u => u.Role == UserRole.None);
    }

    [Fact]
    public void Recorded_camera_list_keeps_all_four_real_users()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pwdgrp-get-p3265v-12.11.txt"));

        var visible = UsersTaskPlugin.VisibleUsers(PwdgrpApi.ParseUsers(body, "root"));

        Assert.Equal(["root", "fakeroot", "root20", "acs"], visible.Select(u => u.Name).ToArray()); // device order (digusers first)
    }

    [Fact]
    public void Axis_os_10_system_accounts_in_the_access_groups_are_not_users()
    {
        // Recorded from an AXIS M3206-LVE on AXIS OS 10.12.323: admin/operator/viewer also contain
        // internal accounts (wwwa*, debugar, sdk, ptzadm, vdo); only digusers holds the real users.
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pwdgrp-get-m3206lve-10.12.txt"));

        var all = PwdgrpApi.ParseUsers(body, "root");
        var visible = UsersTaskPlugin.VisibleUsers(all);

        Assert.Equal(["root", "root2", "root15"], all.Select(u => u.Name).ToArray());
        Assert.Equal(["root", "root2", "root15"], visible.Select(u => u.Name).ToArray());
        Assert.All(visible, u => Assert.Equal(UserRole.Administrator, u.Role));
        Assert.All(visible, u => Assert.True(u.Ptz));
    }
}
