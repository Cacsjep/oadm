using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Users.Tests;

/// <summary>Compatibility gating, write sequence, lock-out protection and secrets, all against <see cref="FakeVapix"/>.</summary>
public sealed class UsersTaskPluginTests
{
    private const string Secret = "S3cret-Passw0rd!";
    private readonly UsersTaskPlugin _plugin = new();

    private static string Payload(UsersMode mode, string user, string? password = null, UserRole role = UserRole.Viewer, bool ptz = false, bool changePassword = false, bool changeRole = false) =>
        UsersJson.Serialize(new UsersPayload { Mode = mode, UserName = user, Password = password, Role = role, Ptz = ptz, ChangePassword = changePassword, ChangeRole = changeRole });

    private static async Task<FakeTaskContext> RunAsync(FakeVapix vapix, string payload, FakeDevice? device = null, FakeTaskContext? ctx = null)
    {
        ctx ??= new FakeTaskContext(vapix);
        var context = ctx;
        await StepRun.RunAsync(context.Steps, () => new UsersTaskPlugin().ExecuteAsync(context, device ?? new FakeDevice(), payload, CancellationToken.None));
        return ctx;
    }

    /// <summary>Detail of the last work step (before the final "Completed" step of a successful task).</summary>
    private static string? LastDetail(FakeTaskContext ctx) => ctx.Steps.Snapshot().Last(s => s.Name != TaskStepList.CompletedStepName).Detail;

    // ---- compatibility ----

    [Fact]
    public void Can_run_on_recorded_device_with_user_management_1_2() => Assert.True(_plugin.CanRun(new FakeDevice()));

    [Fact]
    public void Cannot_run_when_api_list_is_unknown() => Assert.False(_plugin.CanRun(new FakeDevice { Apis = [] }));

    [Fact]
    public void Cannot_run_on_a_different_major_version() =>
        Assert.False(_plugin.CanRun(new FakeDevice { Apis = Fixtures.Apis(("user-management", "2.0")) }));

    [Fact]
    public void Cannot_run_without_credentials() =>
        Assert.False(_plugin.CanRun(new FakeDevice { Status = DeviceStatus.CredentialsRequired }));

    [Fact]
    public void NotSupportedReason_names_the_status_or_the_missing_user_api()
    {
        Assert.Equal("OADM cannot log in to the device: use Log in", _plugin.NotSupportedReason(new FakeDevice { Status = DeviceStatus.CredentialsRequired }));
        Assert.Equal("OADM has not read what this device supports yet: refresh the device", _plugin.NotSupportedReason(new FakeDevice { Apis = [] }));
        Assert.Equal("Needs the user management API (this device has 12.11.77)", _plugin.NotSupportedReason(new FakeDevice { Apis = Fixtures.Apis(("user-management", "2.0")) }));
    }

    [Fact]
    public void Is_a_dialog_task_in_the_context_menu()
    {
        Assert.Equal("oadm.users", _plugin.Id);
        Assert.True(_plugin.RequiresDialog);
        Assert.False(_plugin.ShowInToolbar);
        Assert.Equal("Users", _plugin.DisplayName);
        Assert.Equal(Oadm.Sdk.Plugins.TaskGroups.Users, ((Oadm.Sdk.Plugins.ITaskPlugin)_plugin).Group);
    }

    [Theory]
    [InlineData("systemready", "1.4")]
    [InlineData("user-management", "2.0")]
    [InlineData("user-management", "0.9")]
    public async Task Execute_rechecks_fresh_api_list_before_any_request(string apiId, string version)
    {
        var vapix = new FakeVapix { ApiList = Fixtures.Apis((apiId, version)) };
        var ctx = new FakeTaskContext(vapix);
        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(vapix, Payload(UsersMode.Add, "joe", Secret), ctx: ctx));
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Failed", "Read password policy: Skipped", "Identify OADM account: Skipped", "Read users: Skipped",
             "Validate change: Skipped", "Add user joe: Skipped", "Verify users: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal(1, vapix.ApiListCalls);
        Assert.Empty(vapix.Requests);
    }

    [Fact]
    public async Task Execute_uses_fresh_list_even_when_cached_list_is_fine()
    {
        var vapix = new FakeVapix { ApiList = [] };
        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(vapix, Payload(UsersMode.Remove, "acs"), new FakeDevice()));
        Assert.Empty(vapix.Writes);
    }

    // ---- write sequences ----

    [Fact]
    public async Task Add_reads_state_writes_once_and_verifies()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Add, "joe", Secret, UserRole.Operator, ptz: true));

        Assert.Equal(
            ["axis-cgi/systemready.cgi", "axis-cgi/usergroup.cgi", "axis-cgi/pwdgrp.cgi", "axis-cgi/pwdgrp.cgi", "axis-cgi/pwdgrp.cgi"],
            vapix.Requests.Select(r => r.Uri));
        Assert.Equal(["action=get", null, "action=get"], vapix.Requests.Skip(2).Select(r => r.Body.StartsWith("action=get", StringComparison.Ordinal) ? r.Body : null));
        var write = Assert.Single(vapix.Writes);
        Assert.Equal(HttpMethod.Post, write.Method);
        Assert.Equal("action=add&user=joe&pwd=S3cret-Passw0rd%21&grp=users&sgrp=operator%3Aviewer%3Aptz&comment=", write.Body);
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Done", "Add user joe: Done", "Verify users: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("user-management 1.2", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("Passphrase policy: None", StepRun.Detail(ctx.Steps, "Read password policy"));
        Assert.Equal("OADM uses 'root'", StepRun.Detail(ctx.Steps, "Identify OADM account"));
        Assert.Equal("4 users", StepRun.Detail(ctx.Steps, "Read users"));
        Assert.Equal("User 'joe' added as Operator with PTZ.", LastDetail(ctx));
        Assert.Empty(ctx.Progress); // progress is derived from the steps
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task Change_password_only_sends_pwd_without_sgrp()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Change, "acs", Secret, changePassword: true));
        Assert.Equal("action=update&user=acs&pwd=S3cret-Passw0rd%21", Assert.Single(vapix.Writes).Body);
        Assert.Equal("User 'acs': password changed (Operator).", LastDetail(ctx));
        Assert.Contains("Update user acs: Done", StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Change_role_only_sends_sgrp_without_pwd()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Change, "acs", role: UserRole.Viewer, ptz: true, changeRole: true));
        Assert.Equal("action=update&user=acs&sgrp=viewer%3Aptz", Assert.Single(vapix.Writes).Body);
        Assert.Equal("User 'acs': role is now Viewer with PTZ.", LastDetail(ctx));
    }

    [Fact]
    public async Task Change_to_same_role_writes_nothing()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Change, "acs", role: UserRole.Operator, changeRole: true));
        Assert.Empty(vapix.Writes);
        Assert.Empty(ctx.Warnings);
        Assert.Contains("nothing to change", StepRun.Detail(ctx.Steps, "Validate change"), StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Done", "Update user acs: Skipped", "Verify users: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Nothing to change on this device.", StepRun.Detail(ctx.Steps, "Update user acs"));
    }

    [Fact]
    public async Task Remove_writes_and_verifies()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Remove, "acs"));
        Assert.Equal("action=remove&user=acs", Assert.Single(vapix.Writes).Body);
        Assert.Equal("User 'acs' removed.", LastDetail(ctx));
        Assert.Contains("Remove user acs: Done", StepRun.Lines(ctx.Steps));
        Assert.DoesNotContain("acs", vapix.UsersBody(), StringComparison.Ordinal);
    }

    private static string RemovePayload(params string[] users) =>
        UsersJson.Serialize(new UsersPayload { Mode = UsersMode.Remove, UserNames = users });

    [Fact]
    public async Task Remove_of_several_users_is_one_task_with_one_step_per_user()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, RemovePayload("fakeroot", "acs"));
        Assert.Equal(["action=remove&user=fakeroot", "action=remove&user=acs"], vapix.Writes.Select(w => w.Body));
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Done", "Remove user fakeroot: Done", "Remove user acs: Done", "Verify users: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Users 'fakeroot', 'acs' removed.", LastDetail(ctx));
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task Remove_of_several_users_skips_missing_ones_with_a_warning()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, RemovePayload("ghost", "acs"));
        Assert.Equal("action=remove&user=acs", Assert.Single(vapix.Writes).Body);
        Assert.Contains("'ghost' does not exist", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Warning", "Remove user ghost: Skipped", "Remove user acs: Done", "Verify users: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Remove_of_several_users_including_the_oadm_account_changes_nothing()
    {
        var vapix = new FakeVapix();
        var ctx = new FakeTaskContext(vapix);
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, RemovePayload("acs", "root"), ctx: ctx));
        Assert.Contains("account OADM uses", ex.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Writes);
        Assert.Equal(["Validate change: Failed", "Remove user acs: Skipped", "Remove user root: Skipped", "Verify users: Skipped"], StepRun.Lines(ctx.Steps)[^4..]);
    }

    [Theory]
    [InlineData(UsersMode.Add, "joe", false, false, "Add user joe")]
    [InlineData(UsersMode.Change, "joe", true, false, "Change password joe")]
    [InlineData(UsersMode.Change, "joe", false, true, "Change role joe")]
    [InlineData(UsersMode.Change, "joe", true, true, "Change user joe")]
    [InlineData(UsersMode.Remove, "joe", false, false, "Remove user joe")]
    public void Task_name_says_exactly_what_the_task_does(UsersMode mode, string user, bool changePassword, bool changeRole, string expected)
    {
        var json = Payload(mode, user, Secret, UserRole.Operator, changePassword: changePassword, changeRole: changeRole);
        var name = ((ITaskPlugin)_plugin).GetTaskName(json);
        Assert.Equal(expected, name);
        Assert.DoesNotContain(Secret, name, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_name_of_a_multi_remove_lists_the_users_and_falls_back_without_payload()
    {
        Assert.Equal("Remove users fakeroot, acs", _plugin.GetTaskName(RemovePayload("fakeroot", "acs")));
        Assert.Equal("Remove user acs", _plugin.GetTaskName(RemovePayload("acs")));
        Assert.Equal("Users", _plugin.GetTaskName(null));
        Assert.Equal("Users", _plugin.GetTaskName("{not json"));
    }

    [Fact]
    public async Task Missing_user_on_remove_is_a_warning_not_a_failure()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Remove, "ghost"));
        Assert.Empty(vapix.Writes);
        Assert.Contains("does not exist", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Warning", "Remove user ghost: Skipped", "Verify users: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Existing_user_on_add_is_a_warning()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Add, "acs", Secret));
        Assert.Empty(vapix.Writes);
        Assert.Contains("already exists", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_error_answer_fails_the_device()
    {
        var vapix = new FakeVapix { WriteAnswer = "Error: invalid password." };
        var ctx = new FakeTaskContext(vapix);
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Add, "joe", Secret), ctx: ctx));
        Assert.Contains("invalid password", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["Check compatibility: Done", "Read password policy: Done", "Identify OADM account: Done", "Read users: Done",
             "Validate change: Done", "Add user joe: Failed", "Verify users: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Add user joe"));
    }

    [Fact]
    public async Task Success_answer_without_effect_fails_the_read_back_check()
    {
        var vapix = new FakeVapix { ApplyWrites = false };
        var ctx = new FakeTaskContext(vapix);
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Add, "joe", Secret), ctx: ctx));
        Assert.Contains("not listed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Add user joe: Done", "Verify users: Failed"], StepRun.Lines(ctx.Steps)[^2..]);
    }

    [Fact]
    public async Task Password_violating_device_policy_is_refused_before_writing()
    {
        var vapix = new FakeVapix { SystemReadyBody = """{"data":{"passphrasepolicy":"complex"}}""" };
        var ctx = new FakeTaskContext(vapix);
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Add, "joe", "short"), ctx: ctx));
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Writes);
        Assert.Equal(["Validate change: Failed", "Add user joe: Skipped", "Verify users: Skipped"], StepRun.Lines(ctx.Steps)[^3..]);
    }

    [Fact]
    public async Task Without_systemready_api_the_policy_is_not_queried()
    {
        var vapix = new FakeVapix { ApiList = Fixtures.Apis(("user-management", "1.0")) };
        var ctx = await RunAsync(vapix, Payload(UsersMode.Add, "joe", "x"));
        Assert.DoesNotContain(vapix.Requests, r => r.Uri == "axis-cgi/systemready.cgi");
        Assert.Single(vapix.Writes);
        Assert.Equal("Read password policy: Skipped", StepRun.Lines(ctx.Steps)[1]);
        Assert.Contains("systemready", StepRun.Detail(ctx.Steps, "Read password policy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_payload_fails_without_requests()
    {
        var vapix = new FakeVapix();
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(vapix, null!));
        Assert.Empty(vapix.Requests);
        Assert.Equal(0, vapix.ApiListCalls);
    }

    // ---- lock-out protection end to end ----

    [Fact]
    public async Task Own_account_is_never_removed()
    {
        var vapix = new FakeVapix();
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Remove, "root")));
        Assert.Contains("account OADM uses", ex.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Writes);
    }

    [Fact]
    public async Task Own_account_is_never_demoted_or_given_a_new_password()
    {
        var vapix = new FakeVapix();
        await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Change, "root", role: UserRole.Operator, changeRole: true)));
        await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Change, "root", Secret, changePassword: true)));
        Assert.Empty(vapix.Writes);
    }

    [Fact]
    public async Task Unknown_current_account_blocks_remove()
    {
        var vapix = new FakeVapix { UserGroupStatus = System.Net.HttpStatusCode.NotFound, UserGroupBody = "" };
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(vapix, Payload(UsersMode.Remove, "acs")));
        Assert.Contains("Could not determine", ex.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Writes);
    }

    // ---- secrets ----

    [Fact]
    public async Task Password_never_appears_in_urls_logs_progress_or_errors()
    {
        var vapix = new FakeVapix();
        var ctx = await RunAsync(vapix, Payload(UsersMode.Add, "joe", Secret, UserRole.Administrator));
        var ctx2 = await RunAsync(vapix, Payload(UsersMode.Change, "joe", Secret, changePassword: true));
        var failing = new FakeVapix { WriteAnswer = "Error: invalid password." };
        var ex = await Assert.ThrowsAsync<UserManagementException>(() => RunAsync(failing, Payload(UsersMode.Add, "joe", Secret)));

        Assert.All(vapix.Requests.Concat(failing.Requests), r => Assert.DoesNotContain("S3cret", r.Uri, StringComparison.Ordinal));
        Assert.All(ctx.AllText().Concat(ctx2.AllText()).Append(ex.ToString()), t => Assert.DoesNotContain("S3cret", t, StringComparison.Ordinal));
        Assert.NotEmpty(ctx.Log);
    }

    // ---- query ----

    [Fact]
    public async Task List_users_query_is_read_only_and_returns_recorded_users()
    {
        var vapix = new FakeVapix();
        var json = await _plugin.QueryAsync(new FakeTaskContext(vapix), new FakeDevice(), UsersJson.ListUsersMethod, null, CancellationToken.None);
        var result = UsersJson.ParseQueryResult(json)!;

        Assert.True(result.Supported);
        Assert.Equal("1.2", result.ApiVersion);
        Assert.Equal("root", result.CurrentAccount);
        Assert.Equal(PassphrasePolicy.None, result.Policy);
        Assert.Equal(["root", "fakeroot", "root20", "acs"], result.Users.Select(u => u.Name));
        Assert.True(result.Users[0].IsCurrentAccount);
        Assert.Empty(vapix.Writes);
        Assert.All(vapix.Requests, r => Assert.True(r.Uri != "axis-cgi/pwdgrp.cgi" || r.Body == "action=get"));
    }

    [Fact]
    public async Task List_users_query_on_unsupported_device_does_not_touch_pwdgrp()
    {
        var vapix = new FakeVapix { ApiList = Fixtures.Apis(("user-management", "2.0")) };
        var json = await _plugin.QueryAsync(new FakeTaskContext(vapix), new FakeDevice(), UsersJson.ListUsersMethod, null, CancellationToken.None);
        var result = UsersJson.ParseQueryResult(json)!;
        Assert.False(result.Supported);
        Assert.Contains("2.0", result.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Requests);
    }

    [Fact]
    public async Task Unknown_query_method_is_rejected() =>
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _plugin.QueryAsync(new FakeTaskContext(new FakeVapix()), new FakeDevice(), "removeAll", null, CancellationToken.None));
}
