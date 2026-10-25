using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.Server.AddDevices;

/// <summary>
/// The membership audit of adds and moves (docs/spec/device-scope.md, Decisions "Membership"): a new device "Entered device
/// group X", a device that moved to another route left the groups of its old site and entered those of the new one. One
/// entry per group (<see cref="DeviceGroupMembership"/>). The rows are read back through the system view (the audit covers
/// every group, whoever made the change). Never fails the add or the move it reports on: a failure is logged (only the
/// call's own cancel goes on).
/// </summary>
public static partial class MembershipAudit
{
    /// <summary>The groups the new devices are in (read back from the device table: what was stored counts).</summary>
    public static async Task AddedAsync(DeviceViewFactory? views, AuditLog audit, string userName, string clientAddress,
        IReadOnlyCollection<Guid> deviceIds, string reason, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (views is null || deviceIds.Count == 0)
        {
            return;
        }

        try
        {
            await WriteAddedAsync(views, audit, userName, clientAddress, deviceIds, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailed(logger ?? NullLogger.Instance, ex);
        }
    }

    private static async Task WriteAddedAsync(DeviceViewFactory views, AuditLog audit, string userName, string clientAddress,
        IReadOnlyCollection<Guid> deviceIds, string reason, CancellationToken ct)
    {
        IReadOnlyList<Device> added;
        try
        {
            added = await views.System.GetAsync(deviceIds, ct).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException)
        {
            // One was removed meanwhile: the others still count.
            var wanted = deviceIds.ToHashSet();
            added = [.. (await views.System.ListAsync(ct).ConfigureAwait(false)).Where(d => wanted.Contains(d.Id))];
        }

        var changes = await DeviceGroupMembership.EnteredAsync(views, [.. added], ct).ConfigureAwait(false);
        await DeviceGroupMembership.WriteAsync(audit, userName, clientAddress, changes, Reason(reason), ct).ConfigureAwait(false);
    }

    /// <summary>A device moved (address and route): the groups it left and entered with its new route.</summary>
    public static async Task MovedAsync(DeviceViewFactory? views, AuditLog? audit, Device before, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        try
        {
            if (views is null || audit is null || await views.System.FindAsync(before.Id, ct).ConfigureAwait(false) is not { } after || after.SiteId == before.SiteId)
            {
                return; // the same route: the same groups
            }

            var changes = await DeviceGroupMembership.ChangesAsync(views, [(before, after)], ct).ConfigureAwait(false);
            await DeviceGroupMembership.WriteAsync(audit, "system", string.Empty, changes, "moved", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailed(logger ?? NullLogger.Instance, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The device group audit of an add or a move could not be written")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    private static string Reason(string detail) => string.IsNullOrWhiteSpace(detail) ? "added" : "added, " + detail;
}
