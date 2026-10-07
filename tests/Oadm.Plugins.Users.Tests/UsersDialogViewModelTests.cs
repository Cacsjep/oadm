using Oadm.Plugins.Users.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Users.Tests;

public sealed class UsersDialogViewModelTests
{
    internal static UsersQueryResult RecordedResult() => new(
        true, "1.2", null, "root", PassphrasePolicy.None, PwdgrpApi.ParseUsers(Fixtures.P3265Users, "root"));

    private static UsersDialogViewModel Create(int devices = 1) =>
        new(Enumerable.Range(0, devices).Select(i => (IDeviceInfo)new FakeDevice { Address = $"10.0.0.{48 + i}" }).ToList());

    [Fact]
    public void Starts_in_add_mode_with_viewer_role_and_cannot_apply()
    {
        var vm = Create();
        Assert.True(vm.IsAdd);
        Assert.Equal(UserRole.Viewer, vm.SelectedRole.Role);
        Assert.False(vm.CanApply);
        Assert.False(vm.HasErrors); // untouched form: nothing shown below the fields
        Assert.Equal("Enter a user name.", vm.ApplyBlockedReason); // the tooltip says why
        Assert.True(vm.ShowPassword);
        Assert.True(vm.ShowRole);
    }

    [Fact]
    public void Valid_add_input_builds_the_payload()
    {
        var vm = Create(3);
        vm.UserName = "joe";
        vm.Password = vm.ConfirmPassword = "pw";
        vm.SelectedRole = vm.Roles[1];
        vm.Ptz = true;

        Assert.True(vm.CanApply);
        var payload = UsersJson.ParsePayload(vm.BuildPayload());
        Assert.Equal(UsersMode.Add, payload.Mode);
        Assert.Equal("joe", payload.UserName);
        Assert.Equal("pw", payload.Password);
        Assert.Equal(UserRole.Operator, payload.Role);
        Assert.True(payload.Ptz);
        Assert.Equal("Add user 'joe' as Operator with PTZ on 3 devices. Devices where the user already exists are skipped with a warning.", vm.Summary);
    }

    [Fact]
    public void Mismatched_confirmation_blocks_apply()
    {
        var vm = Create();
        vm.UserName = "joe";
        vm.Password = "pw";
        vm.ConfirmPassword = "pX";
        Assert.False(vm.CanApply);
        Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.ConfirmPassword))); // below the confirmation
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.Null(vm.ErrorOf(nameof(vm.UserName)));
        Assert.Null(vm.BuildPayload());
    }

    [Fact]
    public void Device_policy_from_query_is_enforced_and_hinted()
    {
        var vm = Create(2);
        vm.Apply(RecordedResult() with { Policy = PassphrasePolicy.Complex });
        vm.UserName = "joe";
        vm.Password = vm.ConfirmPassword = "short";
        Assert.False(vm.CanApply);
        Assert.Contains("12", vm.ErrorOf(nameof(vm.Password)), StringComparison.Ordinal);
        Assert.Contains("Every device also checks its own policy", vm.PolicyHint, StringComparison.Ordinal);
        vm.Password = vm.ConfirmPassword = "Abcdefghij1!";
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Remove_mode_picks_the_users_in_the_list_without_a_user_name_field()
    {
        var vm = Create();
        vm.Apply(RecordedResult());
        vm.IsRemove = true;
        Assert.False(vm.ShowUserName);
        Assert.True(vm.MultiSelect);
        Assert.False(vm.ShowPassword);
        Assert.False(vm.ShowRole);
        Assert.False(vm.CanApply);
        Assert.False(vm.HasErrors);
        Assert.Equal("Choose the users to remove in the Existing users list.", vm.ApplyBlockedReason);
        Assert.Equal("Select the users to remove in the Existing users list.", vm.Summary);
        Assert.Equal("Remove user", vm.ApplyText);

        vm.SetSelectedUsers([vm.ExistingUsers[3]]);
        Assert.True(vm.CanApply);
        var payload = UsersJson.ParsePayload(vm.BuildPayload());
        Assert.Equal(UsersMode.Remove, payload.Mode);
        Assert.Equal(["acs"], payload.RemoveNames);
        Assert.Null(payload.Password);
        Assert.Equal("Remove user 'acs' from 1 device. Devices without this user are skipped with a warning.", vm.Summary);

        vm.SetSelectedUsers([vm.ExistingUsers[1], vm.ExistingUsers[3]]);
        payload = UsersJson.ParsePayload(vm.BuildPayload());
        Assert.Equal(["fakeroot", "acs"], payload.UserNames);
        Assert.Equal("Remove users", vm.ApplyText);
        Assert.StartsWith("Remove users 'fakeroot', 'acs' from 1 device.", vm.Summary, StringComparison.Ordinal);
        Assert.Equal("Remove users fakeroot, acs", UsersTaskPlugin.TaskName(payload));
    }

    [Fact]
    public void Protected_accounts_cannot_be_selected_for_removal()
    {
        var vm = Create();
        vm.Apply(RecordedResult());
        var root = vm.ExistingUsers[0];
        Assert.True(root.IsProtected);
        Assert.True(root.IsSelectable); // Add mode: every row can be clicked
        Assert.Null(root.Tooltip);

        vm.IsRemove = true;
        Assert.False(root.IsSelectable);
        Assert.Equal("OADM uses this account for the device; it cannot be removed.", root.Tooltip);
        Assert.All(vm.ExistingUsers.Skip(1), r => Assert.True(r.IsSelectable));

        vm.SetSelectedUsers([root]);
        Assert.Empty(vm.UsersToRemove);
        Assert.False(vm.CanApply);

        vm.IsChange = true;
        Assert.True(root.IsSelectable);
    }

    [Fact]
    public void The_last_administrator_is_protected()
    {
        var vm = Create();
        vm.Apply(new UsersQueryResult(true, "1.2", null, "op", PassphrasePolicy.None,
            [new DeviceUser("boss", UserRole.Administrator, false), new DeviceUser("op", UserRole.Operator, false, IsCurrentAccount: true)]));
        vm.IsRemove = true;
        Assert.Equal("The last administrator of the device cannot be removed.", vm.ExistingUsers[0].Tooltip);
        Assert.Equal("OADM uses this account for the device; it cannot be removed.", vm.ExistingUsers[1].Tooltip);
    }

    [Fact]
    public void Clicking_a_row_in_change_mode_fills_the_user_name()
    {
        var vm = Create();
        vm.Apply(RecordedResult());
        vm.IsChange = true;
        vm.SetSelectedUsers([vm.ExistingUsers[3]]);
        Assert.True(vm.ShowUserName);
        Assert.Equal("acs", vm.UserName);
        vm.UserName = "acs2"; // stays editable
        Assert.Equal("acs2", vm.UserName);
    }

    [Fact]
    public void Change_mode_toggles_password_and_role_sections()
    {
        var vm = Create();
        vm.IsChange = true;
        vm.UserName = "acs";
        Assert.True(vm.ShowPassword);
        Assert.False(vm.ShowRole);
        vm.ChangePassword = false;
        Assert.False(vm.CanApply);
        Assert.Equal("Choose what to change: password, role or both.", vm.ChangeSelectionError); // below the check boxes
        vm.ChangeRole = true;
        Assert.Null(vm.ChangeSelectionError);
        Assert.True(vm.ShowRole);
        Assert.True(vm.CanApply);
        var payload = UsersJson.ParsePayload(vm.BuildPayload());
        Assert.True(payload.ChangeRole);
        Assert.False(payload.ChangePassword);
        Assert.Null(payload.Password);
    }

    [Fact]
    public void Query_result_fills_the_user_list()
    {
        var vm = Create(3);
        vm.Apply(RecordedResult());
        Assert.Equal(["root", "fakeroot", "root20", "acs"], vm.ExistingUsers.Select(u => u.Name));
        Assert.Equal("Administrator with PTZ (used by OADM)", vm.ExistingUsers[0].RoleText);
        Assert.Equal("4 user(s).", vm.UsersStatus);
        Assert.Contains("first of 3 selected devices", vm.UsersTitle, StringComparison.Ordinal);
        Assert.EndsWith("the first of 3 selected devices. 4 user(s).", vm.UsersDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_an_existing_user_switches_to_change_with_its_role()
    {
        var vm = Create();
        vm.Apply(RecordedResult());
        vm.SelectedExistingUser = vm.ExistingUsers[3];
        Assert.True(vm.IsChange);
        Assert.Equal("acs", vm.UserName);
        Assert.Equal(UserRole.Operator, vm.SelectedRole.Role);
        Assert.False(vm.Ptz);
    }

    [Fact]
    public void Own_account_shows_a_lock_out_warning_only_in_change_mode()
    {
        var vm = Create();
        vm.Apply(RecordedResult());
        vm.IsChange = true;
        vm.UserName = "root";
        Assert.Contains("'root' is the account OADM uses", vm.LockOutWarning, StringComparison.Ordinal);

        // No generic warning text: the protection itself is unchanged and enforced by the server.
        vm.UserName = "acs";
        Assert.Null(vm.LockOutWarning);
        vm.IsRemove = true;
        Assert.Null(vm.LockOutWarning);
        vm.IsAdd = true;
        Assert.Null(vm.LockOutWarning);
    }

    [Fact]
    public void Unsupported_device_message_is_shown()
    {
        var vm = Create();
        vm.Apply(new UsersQueryResult(false, "2.0", "This device offers user-management 2.0, which OADM does not support yet.", null, PassphrasePolicy.None, []));
        Assert.Contains("2.0", vm.UsersStatus, StringComparison.Ordinal);
        Assert.Empty(vm.ExistingUsers);
    }

    [Fact]
    public async Task Load_uses_the_query_of_the_first_device()
    {
        var vm = Create(2);
        var ctx = new FakeDialogContext(UsersJson.Serialize(RecordedResult()));
        await vm.LoadAsync(ctx, CancellationToken.None);
        Assert.Equal(UsersJson.ListUsersMethod, ctx.Method);
        Assert.Equal(4, vm.ExistingUsers.Count);
    }

    [Fact]
    public async Task Load_failure_keeps_the_dialog_usable()
    {
        var vm = Create();
        await vm.LoadAsync(new FakeDialogContext(null, new NotSupportedException("Plugin queries are not available yet.")), CancellationToken.None);
        Assert.Contains("could not be loaded", vm.UsersStatus, StringComparison.Ordinal);
        vm.UserName = "joe";
        vm.Password = vm.ConfirmPassword = "pw";
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Apply_and_cancel_raise_close_with_payload_or_null()
    {
        var vm = Create();
        var results = new List<string?>();
        vm.CloseRequested += (_, r) => results.Add(r);
        vm.UserName = "joe";
        vm.Password = vm.ConfirmPassword = "pw";
        vm.ApplyCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0]);
        Assert.Null(results[1]);
    }

    private sealed class FakeDialogContext(string? answer, Exception? error = null) : ITaskDialogContext
    {
        public string? Method { get; private set; }

        public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
        {
            Method = method;
            return error is null ? Task.FromResult(answer) : Task.FromException<string?>(error);
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
