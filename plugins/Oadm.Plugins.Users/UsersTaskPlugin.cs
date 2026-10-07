using Microsoft.Extensions.Logging;

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

    /// <summary>Needs working credentials and pwdgrp.cgi (user-management 1.x) in the cached API list.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown
            && device.Apis.Supports(PwdgrpApi.ApiId, PwdgrpApi.MinVersion);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = UsersJson.ParsePayload(payloadJson);
        var writeStep = WriteStepName(payload);
        ctx.PlanSteps(StepCheckCompatibility, StepReadPolicy, StepIdentifyAccount, StepReadUsers, StepValidate, writeStep, StepVerify);

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
                step.Complete($"OADM uses '{account}'");
            }

            return account;
        }).ConfigureAwait(false);

        var users = await ctx.StepAsync(StepReadUsers, async step =>
        {
            var list = await ReadUsersAsync(ctx.Vapix, current, ct).ConfigureAwait(false);
            step.Complete(list.Count == 1 ? "1 user" : $"{list.Count} users");
            return list;
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Passphrase policy {policy}; OADM account '{current ?? "unknown"}'; {users.Count} user(s) on the device.");

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

    internal const string StepCheckCompatibility = "Check compatibility";
    internal const string StepReadPolicy = "Read password policy";
    internal const string StepIdentifyAccount = "Identify OADM account";
    internal const string StepReadUsers = "Read users";
    internal const string StepValidate = "Validate change";
    internal const string StepVerify = "Verify users";

    /// <summary>The name of the write step: "Add user joe", "Update user joe", "Remove user joe". Never contains the password.</summary>
    internal static string WriteStepName(UsersPayload payload) => payload.Mode switch
    {
        UsersMode.Add => $"Add user {payload.UserName}",
        UsersMode.Remove => $"Remove user {payload.UserName}",
        _ => $"Update user {payload.UserName}",
    };

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
            state.Users));
    }

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
                ? $"User '{userName}' removed."
                : throw new UserManagementException($"The device confirmed the removal, but user '{userName}' is still listed.");
        }

        if (actual is null)
        {
            throw new UserManagementException($"The device confirmed the change, but user '{userName}' is not listed.");
        }

        if (actual.Role != plan.Expected.Role || actual.Ptz != plan.Expected.Ptz)
        {
            throw new UserManagementException($"The device confirmed the change, but user '{userName}' is {UserRoles.Describe(actual.Role, actual.Ptz)} instead of {UserRoles.Describe(plan.Expected.Role, plan.Expected.Ptz)}.");
        }

        var role = UserRoles.Describe(actual.Role, actual.Ptz);
        return mode switch
        {
            UsersMode.Add => $"User '{userName}' added as {role}.",
            _ when plan.SetsPassword && plan.SetsRole => $"User '{userName}': password changed, role is now {role}.",
            _ when plan.SetsRole => $"User '{userName}': role is now {role}.",
            _ => $"User '{userName}': password changed ({role}).",
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
