using Microsoft.Extensions.Logging;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// The caller of a stored user as of now, without a login session (docs/spec/schedules.md "Host: running as the owner"):
/// what <see cref="IUserCalls"/> of the core plugin host builds its call from.
/// </summary>
public interface IUserCallers
{
    /// <summary>The user's caller and view as stored now; null when the account does not exist or is disabled.</summary>
    Task<PluginCaller?> ForUserAsync(string userName, CancellationToken ct);
}

/// <summary>
/// <see cref="IUserCallers"/> from the users table: the account (by name, case-insensitive, enabled), its role's permissions
/// and its device access with the group's current revision, read fresh on every call (never cached here), like the auth
/// interceptor reads a session's. No client address, machine or token: the task owner is the user name and
/// <see cref="AuthenticatedCaller.SessionId"/> is never handed out (<see cref="HostCallContext"/> without a session).
/// </summary>
public sealed class StoredUserCallers(UserStore users, RoleStore roles, DeviceViewFactory views, AccessEpochs epochs) : IUserCallers
{
    public async Task<PluginCaller?> ForUserAsync(string userName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var epoch = epochs.Capture(); // before the account is read: a change after it refuses the call's jobs
        var user = await users.FindByNameAsync(userName, ct).ConfigureAwait(false);
        if (user is null || user.Disabled)
        {
            return null;
        }

        var permissions = await roles.PermissionsOfAsync(user.RoleId, ct).ConfigureAwait(false);
        var devices = await views.CallerDevicesAsync(user.DeviceAccess, ct).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(user.Id, user.UserName, user.RoleId, permissions, string.Empty, string.Empty, string.Empty)
        {
            Devices = devices,
            AccessEpoch = epoch,
        };
        var view = await views.ForAsync(caller, ct).ConfigureAwait(false);
        return new PluginCaller(caller, view);
    }
}

/// <summary>
/// The <see cref="IUserCalls"/> of one bundled core plugin: the user's current caller (<see cref="IUserCallers"/>) as the
/// same <see cref="ICallContext"/> a page call of that plugin gets (devices of the view, VAPIX of the view, tasks started as
/// the user with the queue's view check and the user's MyAxis sign-in), without a session: <see cref="ICallContext.SessionId"/>
/// is empty and no session event reaches it. The device check right before a write reads the user again.
/// </summary>
internal sealed partial class HostUserCalls(IUserCallers callers, Func<PluginCaller, bool, ICallContext> callOf, string pluginId, ILogger logger) : IUserCalls
{
    public async Task<ICallContext?> ForUserAsync(string userName, CancellationToken ct)
    {
        PluginCaller? found;
        try
        {
            found = await callers.ForUserAsync(userName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: a user that cannot be read has no call.
            LogFailed(ex, pluginId);
            return null;
        }

        if (found is null)
        {
            LogNone(pluginId, userName);
            return null;
        }

        var caller = new PluginCaller(found.Caller, found.View)
        {
            RequireCurrentDeviceAccess = (permission, deviceId, token) => RequireNowAsync(found.Caller.UserId, userName, permission, deviceId, token),
        };
        LogCall(pluginId, found.Caller.UserName, found.HasAllDevices);
        return callOf(caller, false);
    }

    /// <summary>The user as stored now must still exist, be enabled, hold the permission and reach the device.</summary>
    private async Task RequireNowAsync(Guid userId, string userName, string permission, Guid deviceId, CancellationToken ct)
    {
        var now = await callers.ForUserAsync(userName, ct).ConfigureAwait(false);
        if (now is null || now.Caller.UserId != userId || !now.Caller.Has(permission))
        {
            throw new UnauthorizedAccessException("The user no longer has this right.");
        }

        await HostCallContext.RequireAsync(now.View, deviceId, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "User call for plugin {PluginId}: {UserName}, all devices {AllDevices}")]
    private partial void LogCall(string pluginId, string userName, bool allDevices);

    [LoggerMessage(Level = LogLevel.Debug, Message = "User call for plugin {PluginId}: {UserName} is no enabled user")]
    private partial void LogNone(string pluginId, string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User call for plugin {PluginId} could not read the user")]
    private partial void LogFailed(Exception ex, string pluginId);
}
