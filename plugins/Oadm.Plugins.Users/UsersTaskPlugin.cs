using Microsoft.Extensions.Logging;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Users;

/// <summary>
/// "Users..." on the device context menu: add a user, change a password and/or role, or remove a
/// user on every selected device through pwdgrp.cgi (API discovery id <c>user-management</c> 1.x).
/// Per device, one named step per request: Check compatibility, Read password policy, Identify OADM
/// account, Read users, Validate change (lock-out protection), the write ("Add user joe", "Update user
/// joe", "Remove user joe"), Verify users. The payload carries the password; it is never logged and
/// never part of a step name or detail.
/// </summary>
public sealed partial class UsersTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.users";

    public string Id => PluginId;

    public string DisplayName => "Users";

    public string Group => TaskGroups.Users;

    public string? IconKey => "users";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    private static readonly Version MinUserManagement = Version.Parse(PwdgrpApi.MinVersion);

    /// <summary>Needs working credentials and pwdgrp.cgi (user-management 1.x) in the cached API list (cheap: called for every device).</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown
            && CachedApiCheck.Supports(device.Apis, PwdgrpApi.ApiId, MinUserManagement);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) ?? TaskSupportReasons.NeedsApi("the user management API", device);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = UsersJson.ParsePayload(payloadJson);
        var writeSteps = WriteStepNames(payload);
        ctx.PlanSteps([StepCheckCompatibility, StepReadPolicy, StepIdentifyAccount, StepReadUsers, StepValidate, .. writeSteps, StepVerify]);

        var apis = await ctx.StepAsync(StepCheckCompatibility, async step =>
        {
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            var api = list.Require(PwdgrpApi.ApiId, PwdgrpApi.MinVersion);
            ctx.Log(TaskLogLevel.Info, $"Device offers {api.Id} {api.Version}; using pwdgrp.cgi.");
            step.Complete($"{api.Id} {api.Version}");
            return list;
        }).ConfigureAwait(false);

        var policy = PassphrasePolicy.None;
        if (apis.Supports(PwdgrpApi.SystemReadyApiId, PwdgrpApi.SystemReadyMinVersion))
        {
            policy = await ctx.StepAsync(StepReadPolicy, async step =>
            {
                var read = await ReadPolicyAsync(ctx.Vapix, ct).ConfigureAwait(false);
                step.Complete($"Passphrase policy: {read}");
                return read;
            }).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(StepReadPolicy, "The device does not offer systemready; no passphrase policy.");
        }

        var current = await ctx.StepAsync(StepIdentifyAccount, async step =>
        {
            var account = await ReadCurrentAccountAsync(ctx.Vapix, ct).ConfigureAwait(false);
            if (account is null)
            {
                step.Complete("Unknown account");
            }
            else
            {
                step.Complete($"OADM uses {account}");
            }

            return account;
        }).ConfigureAwait(false);

        var users = await ctx.StepAsync(StepReadUsers, async step =>
        {
            var list = await ReadUsersAsync(ctx.Vapix, current, ct).ConfigureAwait(false);
            step.Complete(list.Count == 1 ? "1 user" : $"{list.Count} users");
            return list;
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Passphrase policy {policy}; OADM account {current ?? "unknown"}; {users.Count} user(s) on the device.");

        if (payload.Mode == UsersMode.Remove)
        {
            await RemoveAsync(ctx, device, payload, users, current, policy, ct).ConfigureAwait(false);
            return;
        }

        var writeStep = writeSteps[0];
        UserChangePlan plan;
        using (var step = ctx.BeginStep(StepValidate))
        {
            plan = UserChangePlanner.Plan(payload, users, current, policy, device.FirmwareVersion);
            if (plan.Kind == PlanKind.Skip)
            {
                if (plan.IsWarning)
                {
                    step.Warn(plan.Message);
                }
                else
                {
                    ctx.Log(TaskLogLevel.Info, plan.Message);
                    step.Complete(plan.Message);
                }
            }
            else
            {
                step.Complete(plan.Message);
            }
        }

        if (plan.Kind == PlanKind.Skip)
        {
            ctx.SkipStep(writeStep, "Nothing to change on this device.");
            ctx.SkipStep(StepVerify, "Nothing was changed.");
            return;
        }

        ctx.Log(TaskLogLevel.Info, plan.Message);
        LogWriting(ctx.Logger, payload.Mode, payload.UserName, device.Serial);
        using (ctx.BeginStep(writeStep))
        {
            var (request, verb) = BuildWrite(payload, plan);
            using (request)
            {
                using var response = await ctx.Vapix.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                PwdgrpApi.EnsureWriteSucceeded((int)response.StatusCode, body, verb);
            }
        }

        await ctx.StepAsync(StepVerify, async step =>
        {
            var after = await ReadUsersAsync(ctx.Vapix, current, ct).ConfigureAwait(false);
            var done = Verify(payload.Mode, payload.UserName, plan, after);
            ctx.Log(TaskLogLevel.Info, done);
            step.Complete(done);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove mode: validates every user first (a protected one fails the task before anything is removed),
    /// then one "Remove user x" step per user (missing ones Skipped, the Validate step ends with a warning),
    /// then one verification of the whole list.
    /// </summary>
    private static async Task RemoveAsync(
        ITaskExecutionContext ctx,
        IDeviceInfo device,
        UsersPayload payload,
        IReadOnlyList<DeviceUser> users,
        string? current,
        PassphrasePolicy policy,
        CancellationToken ct)
    {
        IReadOnlyList<UserRemoval> removals;
        using (var step = ctx.BeginStep(StepValidate))
        {
            UserChangePlanner.ValidatePayload(payload, policy);
            removals = UserChangePlanner.PlanRemoval(payload.RemoveNames, users, current, device.FirmwareVersion);
            var missing = removals.Where(r => r.Plan.Kind == PlanKind.Skip).Select(r => r.Plan.Message).ToList();
            var writes = removals.Where(r => r.Plan.Kind == PlanKind.Write).Select(r => r.Name).ToList();
            var summary = writes.Count == 0 ? null : writes.Count == 1 ? $"Remove user {writes[0]}" : $"Remove users {string.Join(", ", writes)}";
            if (missing.Count > 0)
            {
                step.Warn(string.Join(" ", missing));
            }
            else
            {
                step.Complete(summary);
            }

            if (summary is not null)
            {
                ctx.Log(TaskLogLevel.Info, summary);
            }
        }

        var removed = new List<string>();
        foreach (var removal in removals)
        {
            var name = RemoveStepName(removal.Name);
            if (removal.Plan.Kind == PlanKind.Skip)
            {
                ctx.SkipStep(name, $"User {removal.Name} does not exist on this device.");
                continue;
            }

            LogWriting(ctx.Logger, UsersMode.Remove, removal.Name, device.Serial);
            using (ctx.BeginStep(name))
            {
                using var request = PwdgrpApi.BuildRemove(removal.Name);
                using var response = await ctx.Vapix.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                PwdgrpApi.EnsureWriteSucceeded((int)response.StatusCode, body, "Removed");
            }

            removed.Add(removal.Name);
        }

        if (removed.Count == 0)
        {
            ctx.SkipStep(StepVerify, "Nothing was changed.");
            return;
        }

        await ctx.StepAsync(StepVerify, async step =>
        {
            var after = await ReadUsersAsync(ctx.Vapix, current, ct).ConfigureAwait(false);
            var still = removed.Where(n => after.Any(u => string.Equals(u.Name, n, StringComparison.Ordinal))).ToList();
            if (still.Count > 0)
            {
                throw new UserManagementException($"The device confirmed the removal, but user {string.Join(", ", still)} is still listed.");
            }

            var done = removed.Count == 1 ? $"User {removed[0]} removed." : $"Users {string.Join(", ", removed)} removed.";
            ctx.Log(TaskLogLevel.Info, done);
            step.Complete(done);
        }).ConfigureAwait(false);
    }

    internal const string StepCheckCompatibility = "Check compatibility";
    internal const string StepReadPolicy = "Read password policy";
    internal const string StepIdentifyAccount = "Identify OADM account";
    internal const string StepReadUsers = "Read users";
    internal const string StepValidate = "Validate change";
    internal const string StepVerify = "Verify users";

    /// <summary>The name of the write step: "Add user joe", "Update user joe", "Remove user joe". Never contains the password.</summary>
    internal static string WriteStepName(UsersPayload payload) => WriteStepNames(payload)[0];

    /// <summary>The write steps: one for Add and Change, one "Remove user x" per user for Remove.</summary>
    internal static IReadOnlyList<string> WriteStepNames(UsersPayload payload) => payload.Mode switch
    {
        UsersMode.Add => [$"Add user {payload.UserName}"],
        UsersMode.Remove => payload.RemoveNames.Count == 0 ? [RemoveStepName(payload.UserName)] : [.. payload.RemoveNames.Select(RemoveStepName)],
        _ => [$"Update user {payload.UserName}"],
    };

    private static string RemoveStepName(string userName) => $"Remove user {userName}";

    /// <summary>
    /// The task name: "Add user joe", "Change password joe" (only the password), "Change role joe" (only role
    /// or PTZ), "Change user joe" (both), "Remove user joe" / "Remove users joe, ann". Never the password.
    /// </summary>
    public string GetTaskName(string? payloadJson)
    {
        UsersPayload payload;
        try
        {
            payload = UsersJson.ParsePayload(payloadJson);
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }

        return TaskName(payload) ?? DisplayName;
    }

    /// <summary>See <see cref="GetTaskName"/>; null when the payload names no user.</summary>
    public static string? TaskName(UsersPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Mode == UsersMode.Remove)
        {
            var names = payload.RemoveNames;
            return names.Count switch
            {
                0 => null,
                1 => $"Remove user {names[0]}",
                _ => $"Remove users {string.Join(", ", names)}",
            };
        }

        var name = payload.UserName.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return payload.Mode switch
        {
            UsersMode.Add => $"Add user {name}",
            UsersMode.Change when payload.ChangePassword && !payload.ChangeRole => $"Change password {name}",
            UsersMode.Change when payload.ChangeRole && !payload.ChangePassword => $"Change role {name}",
            UsersMode.Change => $"Change user {name}",
            _ => null,
        };
    }

    /// <summary>Read-only. <c>listUsers</c> returns <see cref="UsersQueryResult"/> JSON for the dialog.</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, UsersJson.ListUsersMethod, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query '{method}'.");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        if (!apis.Supports(PwdgrpApi.ApiId, PwdgrpApi.MinVersion))
        {
            var found = apis.FindApi(PwdgrpApi.ApiId)?.Version;
            var message = found is null
                ? "This device does not offer user management (user-management 1.x)."
                : $"This device offers user-management {found}, which OADM does not support yet.";
            return UsersJson.Serialize(new UsersQueryResult(false, found, message, null, PassphrasePolicy.None, []));
        }

        var state = await ReadStateAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
        return UsersJson.Serialize(new UsersQueryResult(
            true,
            apis.FindApi(PwdgrpApi.ApiId, 1)?.Version,
            state.CurrentAccount is null ? "Could not determine the account OADM uses; changing or removing users will be refused." : null,
            state.CurrentAccount,
            state.Policy,
            VisibleUsers(state.Users)));
    }

    /// <summary>
    /// The users the dialog shows: only accounts in the admin (root), operator or viewer group.
    /// Accounts in none of them (e.g. SSH-only or service accounts listed in digusers) are hidden;
    /// the safety checks still run against the full list.
    /// </summary>
    internal static IReadOnlyList<DeviceUser> VisibleUsers(IEnumerable<DeviceUser> users) =>
        users.Where(u => u.Role != UserRole.None).ToList();

    internal static (HttpRequestMessage Request, string Verb) BuildWrite(UsersPayload payload, UserChangePlan plan) => payload.Mode switch
    {
        UsersMode.Add => (PwdgrpApi.BuildAdd(payload.UserName, payload.Password!, payload.Role, payload.Ptz), "Created"),
        UsersMode.Change => (PwdgrpApi.BuildUpdate(
            payload.UserName,
            plan.SetsPassword ? payload.Password : null,
            plan.SetsRole ? payload.Role : null,
            payload.Ptz), "Modified"),
        UsersMode.Remove => (PwdgrpApi.BuildRemove(payload.UserName), "Removed"),
        _ => throw new UserManagementException($"Unknown mode {payload.Mode}. Nothing was changed."),
    };

    /// <summary>Checks the read-back list and returns the success message for the device.</summary>
    internal static string Verify(UsersMode mode, string userName, UserChangePlan plan, IReadOnlyList<DeviceUser> after)
    {
        var actual = after.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.Ordinal));
        if (plan.Expected is null)
        {
            return actual is null
                ? $"User {userName} removed."
                : throw new UserManagementException($"The device confirmed the removal, but user {userName} is still listed.");
        }

        if (actual is null)
        {
            throw new UserManagementException($"The device confirmed the change, but user {userName} is not listed.");
        }

        if (actual.Role != plan.Expected.Role || actual.Ptz != plan.Expected.Ptz)
        {
            throw new UserManagementException($"The device confirmed the change, but user {userName} is {UserRoles.Describe(actual.Role, actual.Ptz)} instead of {UserRoles.Describe(plan.Expected.Role, plan.Expected.Ptz)}.");
        }

        var role = UserRoles.Describe(actual.Role, actual.Ptz);
        return mode switch
        {
            UsersMode.Add => $"User {userName} added as {role}.",
            _ when plan.SetsPassword && plan.SetsRole => $"User {userName}: password changed, role is now {role}.",
            _ when plan.SetsRole => $"User {userName}: role is now {role}.",
            _ => $"User {userName}: password changed ({role}).",
        };
    }

    private sealed record DeviceState(IReadOnlyList<DeviceUser> Users, string? CurrentAccount, PassphrasePolicy Policy);

    private static async Task<DeviceState> ReadStateAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        var policy = apis.Supports(PwdgrpApi.SystemReadyApiId, PwdgrpApi.SystemReadyMinVersion)
            ? await ReadPolicyAsync(vapix, ct).ConfigureAwait(false)
            : PassphrasePolicy.None;
        var current = await ReadCurrentAccountAsync(vapix, ct).ConfigureAwait(false);
        var users = await ReadUsersAsync(vapix, current, ct).ConfigureAwait(false);
        return new DeviceState(users, current, policy);
    }

    private static async Task<PassphrasePolicy> ReadPolicyAsync(IVapixClient vapix, CancellationToken ct) =>
        PwdgrpApi.ParsePassphrasePolicy(await SendForStringAsync(vapix, PwdgrpApi.BuildSystemReady(), ct).ConfigureAwait(false) ?? string.Empty);

    private static async Task<string?> ReadCurrentAccountAsync(IVapixClient vapix, CancellationToken ct) =>
        PwdgrpApi.ParseCurrentAccount(await SendForStringAsync(vapix, PwdgrpApi.BuildCurrentAccount(), ct).ConfigureAwait(false) ?? string.Empty);

    private static async Task<IReadOnlyList<DeviceUser>> ReadUsersAsync(IVapixClient vapix, string? currentAccount, CancellationToken ct)
    {
        var body = await SendForStringAsync(vapix, PwdgrpApi.BuildGet(), ct).ConfigureAwait(false)
            ?? throw new UserManagementException("The device did not return its user list. Nothing was changed.");
        return PwdgrpApi.ParseUsers(body, currentAccount);
    }

    /// <returns>The body of a 2xx response, otherwise null.</returns>
    private static async Task<string?> SendForStringAsync(IVapixClient vapix, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Users task: {Mode} '{UserName}' on {Serial}")]
    private static partial void LogWriting(ILogger logger, UsersMode mode, string userName, string serial);
}
