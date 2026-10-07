namespace Oadm.Plugins.Users.Tests;

/// <summary>Lock-out protection and existence checks, independent of HTTP.</summary>
public sealed class UserChangePlannerTests
{
    [Fact]
    public void Removing_several_administrators_keeps_the_last_one()
    {
        IReadOnlyList<DeviceUser> users =
        [
            new("a", UserRole.Administrator, false), new("b", UserRole.Administrator, false), new("oadm", UserRole.Operator, false),
        ];

        var ex = Assert.Throws<UserManagementException>(() => UserChangePlanner.PlanRemoval(["a", "b"], users, "oadm", "12.11.77"));
        Assert.Contains("'b' is the last administrator", ex.Message, StringComparison.Ordinal);

        var plans = UserChangePlanner.PlanRemoval(["a", "ghost"], users, "oadm", "12.11.77");
        Assert.Equal([PlanKind.Write, PlanKind.Skip], plans.Select(p => p.Plan.Kind));
        Assert.True(plans[1].Plan.IsWarning);
    }

    [Fact]
    public void Remove_payload_needs_at_least_one_valid_user()
    {
        Assert.Throws<UserManagementException>(() => UserChangePlanner.ValidatePayload(new UsersPayload { Mode = UsersMode.Remove }, PassphrasePolicy.None));
        Assert.Throws<UserManagementException>(() => UserChangePlanner.ValidatePayload(new UsersPayload { Mode = UsersMode.Remove, UserNames = ["ok", "bad name!"] }, PassphrasePolicy.None));
        UserChangePlanner.ValidatePayload(new UsersPayload { Mode = UsersMode.Remove, UserNames = ["a", "a", "b"] }, PassphrasePolicy.None);
        Assert.Equal(["a", "b"], new UsersPayload { Mode = UsersMode.Remove, UserNames = ["a", " a", "b"] }.RemoveNames);
    }

    private static readonly IReadOnlyList<DeviceUser> Users =
    [
        new("root", UserRole.Administrator, true, true),
        new("admin2", UserRole.Administrator, false),
        new("op", UserRole.Operator, false),
    ];

    private static readonly IReadOnlyList<DeviceUser> SingleAdmin =
    [
        new("boss", UserRole.Administrator, true),
        new("op", UserRole.Operator, false),
    ];

    private static UserChangePlan Plan(UsersPayload p, IReadOnlyList<DeviceUser>? users = null, string? current = "root", string? firmware = "12.11.77") =>
        UserChangePlanner.Plan(p, users ?? Users, current, PassphrasePolicy.None, firmware);

    private static UsersPayload Remove(string user) => new() { Mode = UsersMode.Remove, UserName = user };

    private static UsersPayload Demote(string user) => new() { Mode = UsersMode.Change, UserName = user, Role = UserRole.Operator, ChangeRole = true };

    [Fact]
    public void Removing_the_current_account_is_refused() =>
        Assert.Throws<UserManagementException>(() => Plan(Remove("root")));

    [Fact]
    public void Current_account_match_ignores_case() =>
        Assert.Throws<UserManagementException>(() => Plan(Remove("root"), current: "ROOT"));

    [Fact]
    public void Demoting_the_current_account_is_refused() =>
        Assert.Throws<UserManagementException>(() => Plan(Demote("root")));

    [Fact]
    public void Changing_the_current_account_password_is_refused() =>
        Assert.Throws<UserManagementException>(() => Plan(new UsersPayload { Mode = UsersMode.Change, UserName = "root", Password = "x", ChangePassword = true }));

    [Fact]
    public void Toggling_ptz_on_the_current_admin_account_is_allowed()
    {
        var plan = Plan(new UsersPayload { Mode = UsersMode.Change, UserName = "root", Role = UserRole.Administrator, Ptz = false, ChangeRole = true });
        Assert.Equal(PlanKind.Write, plan.Kind);
    }

    [Fact]
    public void Removing_the_last_admin_is_refused()
    {
        var ex = Assert.Throws<UserManagementException>(() => Plan(Remove("boss"), SingleAdmin, current: "op2"));
        Assert.Contains("last administrator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demoting_the_last_admin_is_refused() =>
        Assert.Throws<UserManagementException>(() => Plan(Demote("boss"), SingleAdmin, current: "op2"));

    [Fact]
    public void Removing_another_admin_is_allowed() => Assert.Equal(PlanKind.Write, Plan(Remove("admin2")).Kind);

    [Fact]
    public void Demoting_another_admin_is_allowed() => Assert.Equal(PlanKind.Write, Plan(Demote("admin2")).Kind);

    [Theory]
    [InlineData("11.4.63", true)]
    [InlineData("10.12.1", true)]
    [InlineData(null, true)]
    [InlineData("11.5.64", false)]
    [InlineData("12.11.77", false)]
    public void Root_can_only_be_removed_since_axis_os_11_5(string? firmware, bool refused)
    {
        IReadOnlyList<DeviceUser> users = [new("root", UserRole.Administrator, true), new("other", UserRole.Administrator, true, true)];
        if (refused)
        {
            Assert.Throws<UserManagementException>(() => Plan(Remove("root"), users, current: "other", firmware: firmware));
        }
        else
        {
            Assert.Equal(PlanKind.Write, Plan(Remove("root"), users, current: "other", firmware: firmware).Kind);
        }
    }

    [Fact]
    public void Unknown_current_account_refuses_change_and_remove_but_allows_add()
    {
        Assert.Throws<UserManagementException>(() => Plan(Remove("op"), current: null));
        Assert.Throws<UserManagementException>(() => Plan(Demote("admin2"), current: null));
        Assert.Equal(PlanKind.Write, Plan(new UsersPayload { Mode = UsersMode.Add, UserName = "new", Password = "x" }, current: null).Kind);
    }

    [Fact]
    public void Missing_user_is_a_warning_skip()
    {
        var plan = Plan(Remove("ghost"));
        Assert.Equal(PlanKind.Skip, plan.Kind);
        Assert.True(plan.IsWarning);
    }

    [Fact]
    public void Change_without_anything_selected_is_invalid() =>
        Assert.Throws<UserManagementException>(() => Plan(new UsersPayload { Mode = UsersMode.Change, UserName = "op" }));

    [Fact]
    public void Invalid_user_name_is_refused_before_anything_else() =>
        Assert.Throws<UserManagementException>(() => Plan(new UsersPayload { Mode = UsersMode.Remove, UserName = "a b" }));
}
