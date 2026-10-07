using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Users;

/// <summary>
/// "Users..." on the device context menu: add a user, change a password and/or role, or remove a
/// user on every selected device through pwdgrp.cgi (API discovery id <c>user-management</c> 1.x).
/// Per device: fresh API check, read policy, current account and users, plan (validation and
/// lock-out protection), one write, read back and verify. The payload carries the password; it is
/// never logged.
/// </summary>
public sealed partial class UsersTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.users";

    public string Id => PluginId;

    public string DisplayName => "Users...";

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

        ctx.ReportProgress(0, "Checking device compatibility");
        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        var api = apis.Require(PwdgrpApi.ApiId, PwdgrpApi.MinVersion);
        ctx.Log(TaskLogLevel.Info, $"Device offers {api.Id} {api.Version}; using pwdgrp.cgi.");

        ctx.ReportProgress(15, "Reading users");
        var state = await ReadStateAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Passphrase policy {state.Policy}; OADM account '{state.CurrentAccount ?? "unknown"}'; {state.Users.Count} user(s) on the device.");

        var plan = UserChangePlanner.Plan(payload, state.Users, state.CurrentAccount, state.Policy, device.FirmwareVersion);
        if (plan.Kind == PlanKind.Skip)
        {
            if (plan.IsWarning)
            {
                ctx.ReportWarning(plan.Message);
            }
            else
            {
                ctx.Log(TaskLogLevel.Info, plan.Message);
            }

            ctx.ReportProgress(100, plan.Message);
            return;
        }

        ctx.ReportProgress(40, plan.Message);
        ctx.Log(TaskLogLevel.Info, plan.Message);
        LogWriting(ctx.Logger, payload.Mode, payload.UserName, device.Serial);
        var (request, verb) = BuildWrite(payload, plan);
        using (request)
        {
            using var response = await ctx.Vapix.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            PwdgrpApi.EnsureWriteSucceeded((int)response.StatusCode, body, verb);
        }

        ctx.ReportProgress(75, "Verifying");
        var after = await ReadUsersAsync(ctx.Vapix, state.CurrentAccount, ct).ConfigureAwait(false);
        var done = Verify(payload.Mode, payload.UserName, plan, after);
        ctx.Log(TaskLogLevel.Info, done);
        ctx.ReportProgress(100, done);
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
            ? PwdgrpApi.ParsePassphrasePolicy(await SendForStringAsync(vapix, PwdgrpApi.BuildSystemReady(), ct).ConfigureAwait(false) ?? string.Empty)
            : PassphrasePolicy.None;
        var current = PwdgrpApi.ParseCurrentAccount(await SendForStringAsync(vapix, PwdgrpApi.BuildCurrentAccount(), ct).ConfigureAwait(false) ?? string.Empty);
        var users = await ReadUsersAsync(vapix, current, ct).ConfigureAwait(false);
        return new DeviceState(users, current, policy);
    }

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
