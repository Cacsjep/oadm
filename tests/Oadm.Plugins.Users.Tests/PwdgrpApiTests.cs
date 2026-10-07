namespace Oadm.Plugins.Users.Tests;

/// <summary>Exact requests and parsing of recorded read-only responses (AXIS P3265-V, AXIS OS 12.11).</summary>
public sealed class PwdgrpApiTests
{
    private const string Secret = "Pa ss+w&rd=1%";

    private static async Task<RecordedRequest> RecordAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var body = await request.Content!.ReadAsStringAsync();
            return new RecordedRequest(request.Method, request.RequestUri!.ToString(), request.Content.Headers.ContentType?.MediaType, body);
        }
    }

    [Fact]
    public async Task Get_is_a_form_post_without_query_string()
    {
        var r = await RecordAsync(PwdgrpApi.BuildGet());
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("axis-cgi/pwdgrp.cgi", r.Uri);
        Assert.Equal("application/x-www-form-urlencoded", r.ContentType);
        Assert.Equal("action=get", r.Body);
    }

    [Fact]
    public async Task Add_puts_password_and_groups_in_the_body_only()
    {
        var r = await RecordAsync(PwdgrpApi.BuildAdd("joe", Secret, UserRole.Operator, ptz: true));
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("axis-cgi/pwdgrp.cgi", r.Uri);
        Assert.DoesNotContain('?', r.Uri);
        Assert.Equal("application/x-www-form-urlencoded", r.ContentType);
        Assert.Equal("action=add&user=joe&pwd=Pa+ss%2Bw%26rd%3D1%25&grp=users&sgrp=operator%3Aviewer%3Aptz&comment=", r.Body);
    }

    [Theory]
    [InlineData(UserRole.Administrator, false, "admin:operator:viewer")]
    [InlineData(UserRole.Administrator, true, "admin:operator:viewer:ptz")]
    [InlineData(UserRole.Operator, false, "operator:viewer")]
    [InlineData(UserRole.Viewer, false, "viewer")]
    [InlineData(UserRole.Viewer, true, "viewer:ptz")]
    public void Roles_map_to_vapix_access_groups(UserRole role, bool ptz, string sgrp) =>
        Assert.Equal(sgrp, UserRoles.ToSecondaryGroups(role, ptz));

    [Fact]
    public async Task Update_password_only()
    {
        var r = await RecordAsync(PwdgrpApi.BuildUpdate("joe", "newpass", null, ptz: true));
        Assert.Equal("axis-cgi/pwdgrp.cgi", r.Uri);
        Assert.Equal("action=update&user=joe&pwd=newpass", r.Body);
    }

    [Fact]
    public async Task Update_role_only()
    {
        var r = await RecordAsync(PwdgrpApi.BuildUpdate("joe", null, UserRole.Viewer, ptz: false));
        Assert.Equal("action=update&user=joe&sgrp=viewer", r.Body);
    }

    [Fact]
    public async Task Update_password_and_role()
    {
        var r = await RecordAsync(PwdgrpApi.BuildUpdate("joe", "newpass", UserRole.Administrator, ptz: true));
        Assert.Equal("action=update&user=joe&pwd=newpass&sgrp=admin%3Aoperator%3Aviewer%3Aptz", r.Body);
    }

    [Fact]
    public void Update_without_changes_is_rejected() =>
        Assert.Throws<ArgumentException>(() => PwdgrpApi.BuildUpdate("joe", null, null, ptz: false));

    [Fact]
    public async Task Remove()
    {
        var r = await RecordAsync(PwdgrpApi.BuildRemove("joe"));
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("action=remove&user=joe", r.Body);
    }

    [Fact]
    public void Current_account_request_is_a_plain_get()
    {
        using var request = PwdgrpApi.BuildCurrentAccount();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("axis-cgi/usergroup.cgi", request.RequestUri!.ToString());
    }

    [Fact]
    public void Recorded_user_list_is_parsed_with_roles_and_ptz()
    {
        var users = PwdgrpApi.ParseUsers(Fixtures.P3265Users, currentAccount: "root");

        Assert.Equal(["root", "fakeroot", "root20", "acs"], users.Select(u => u.Name));
        Assert.Equal(new DeviceUser("root", UserRole.Administrator, true, true), users[0]);
        Assert.Equal(new DeviceUser("fakeroot", UserRole.Administrator, true), users[1]);
        Assert.Equal(new DeviceUser("root20", UserRole.Administrator, true), users[2]);
        Assert.Equal(new DeviceUser("acs", UserRole.Operator, false), users[3]);
    }

    [Fact]
    public void User_list_without_digusers_still_lists_group_members()
    {
        var users = PwdgrpApi.ParseUsers("admin=\"root\"\noperator=\"root,ann\"\nviewer=\"root,ann,bob\"\nptz=\"bob\"\n");
        Assert.Equal(["root", "ann", "bob"], users.Select(u => u.Name));
        Assert.Equal(new DeviceUser("bob", UserRole.Viewer, true), users[2]);
    }

    [Fact]
    public void Empty_groups_parse_to_no_users() =>
        Assert.Empty(PwdgrpApi.ParseUsers("admin=\"\"\noperator=\"\"\nviewer=\"\"\nptz=\"\"\ndigusers=\"\"\n"));

    [Fact]
    public void Garbage_user_list_is_refused() =>
        Assert.Throws<UserManagementException>(() => PwdgrpApi.ParseUsers("<html>Not found</html>"));

    [Fact]
    public void Recorded_usergroup_gives_the_current_account() =>
        Assert.Equal("root", PwdgrpApi.ParseCurrentAccount(Fixtures.P3265UserGroup));

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>401</body></html>")]
    public void Unusable_usergroup_gives_null(string body) => Assert.Null(PwdgrpApi.ParseCurrentAccount(body));

    [Fact]
    public void Recorded_systemready_gives_the_policy() =>
        Assert.Equal(PassphrasePolicy.None, PwdgrpApi.ParsePassphrasePolicy(Fixtures.P3265SystemReady));

    [Theory]
    [InlineData("""{"data":{"passphrasepolicy":"complex"}}""", PassphrasePolicy.Complex)]
    [InlineData("""{"data":{"passphrasepolicy":"length"}}""", PassphrasePolicy.Length)]
    [InlineData("""{"data":{"systemready":"yes"}}""", PassphrasePolicy.None)]
    [InlineData("not json", PassphrasePolicy.None)]
    public void Systemready_policy_variants(string json, PassphrasePolicy expected) =>
        Assert.Equal(expected, PwdgrpApi.ParsePassphrasePolicy(json));

    [Theory]
    [InlineData("Created account joe.", "Created")]
    [InlineData("Modified account joe.", "Modified")]
    [InlineData("Modified the account joe.", "Modified")]
    [InlineData("<html><body>Removed account joe.</body></html>", "Removed")]
    public void Write_success_answers(string body, string verb) => PwdgrpApi.EnsureWriteSucceeded(200, body, verb);

    [Theory]
    [InlineData(200, "Error: invalid password.")]
    [InlineData(200, "Error: account user name.")]
    [InlineData(200, "Something else")]
    [InlineData(401, "")]
    [InlineData(500, "boom")]
    public void Write_failures_throw(int status, string body) =>
        Assert.Throws<UserManagementException>(() => PwdgrpApi.EnsureWriteSucceeded(status, body, "Created"));
}
