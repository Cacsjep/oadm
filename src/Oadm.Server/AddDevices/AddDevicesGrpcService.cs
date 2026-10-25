using System.Collections.Concurrent;
using System.Net;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Discovery;
using Oadm.Server.Mapping;
using Oadm.Server.Tasks;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>
/// gRPC AddDevicesService, the server side of the add devices page.
/// <para><b>RetryAuth</b> logs in to one discovered device with credentials the technician typed
/// (<see cref="DiscoveryAuthenticator"/>); the automatic login with known credentials runs while
/// the page watches discovery.</para>
/// <para><b>Commit</b> stores each device (serial unique; the entered address of "Add manually",
/// else the verified DNS name when the request asks for DNS names and the device has one, else the IP), pins the certificate
/// fingerprint from the probe, sets the initial root password on factory-default devices when one
/// is given (per device from <c>initial_passwords</c>, else <c>initial_root_password</c>; over HTTPS
/// when the device offers it), stores credentials (initial password as root, else explicit request
/// credentials, else the credential that worked in the session's login), verifies them with one
/// authenticated call (wrong credentials still add the device, status CredentialsRequired) and
/// finally queues the first full refresh. Adding devices is not a task and never shows up in the
/// task list. <b>Prepare</b> is the legacy wizard step, kept for wire compatibility.</para>
/// <para>Through the caller (docs/spec/device-scope.md, Decisions "New devices", "Add and discovery sessions"): only the
/// caller's own sessions (another user's session id answers NOT_FOUND like an unknown one); a user with a device group adds
/// only devices found at the sites of the group that land inside it, logs in only with what was typed (never the credential
/// list), and the new devices remember the group (<c>AddedByGroupId</c>); a user with all devices adds as before
/// (<c>AddedByAllDevices</c>). The groups the new devices entered are audited.</para>
/// </summary>
public sealed partial class AddDevicesGrpcService(
    IDiscoveryBackend discovery,
    ServerSettingsStore settings,
    DeviceActions actions,
    DiscoveryAuthenticator authenticator,
    DeviceAdder adder,
    DeviceViewFactory views,
    DiscoverySessionOwners owners,
    AuditLog audit,
    ILogger<AddDevicesGrpcService> logger,
    Core.Sites.ISiteStates? states = null) : Proto.AddDevicesService.AddDevicesServiceBase
{
    /// <summary>Devices handled at the same time during Prepare and Commit.</summary>
    public const int Parallelism = 8;

    public override async Task<Proto.AddPlan> Prepare(Proto.PrepareRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var reach = await ReachAsync(context).ConfigureAwait(false);
        var selected = await ResolveAsync(request.SessionId, request.DiscoveredIds, reach, context).ConfigureAwait(false);
        var items = new ConcurrentDictionary<string, Proto.AddPlanItem>();
        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (found, token) =>
        {
            var probed = await adder.TryProbeAsync(found, token).ConfigureAwait(false);
            var factoryDefault = DeviceAdder.IsFactoryDefault(found, probed);
            items[found.DiscoveredId] = new Proto.AddPlanItem
            {
                DiscoveredId = found.DiscoveredId,
                Serial = found.Serial,
                Address = found.Address.ToString(),
                HostName = found.HostName ?? string.Empty,
                Model = probed?.Model ?? found.Model ?? string.Empty,
                NeedsInitialPassword = factoryDefault,
                NeedsCredentials = !factoryDefault,
            };
        }).ConfigureAwait(false);

        var plan = new Proto.AddPlan();
        plan.Items.AddRange(selected.Select(s => items[s.DiscoveredId]));
        return plan;
    }

    public override async Task<Proto.DiscoveredDevice> RetryAuth(Proto.RetryAuthRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            throw GrpcGuard.InvalidArgument("Enter a user name and a password.");
        }

        var reach = await ReachAsync(context).ConfigureAwait(false);
        var caller = AuthInterceptor.CallerOf(context);
        var found = (owners.Owns(request.SessionId, caller) ? discovery.FindDiscovered(request.SessionId, request.DiscoveredId) : null) is { } known
            && reach.Allows(known.Route)
            ? known
            : throw GrpcGuard.NotFound($"Discovered device '{request.DiscoveredId}' is unknown (discovery session '{request.SessionId}').");
        if (await adder.IsManagedAsync(found.Serial, ct).ConfigureAwait(false))
        {
            throw GrpcGuard.FailedPrecondition($"The device {found.Serial} has already been added.");
        }

        try
        {
            // The credential is tried on the failed devices of the caller's own sessions only.
            var result = await authenticator.RetryAsync(request.SessionId, found, request.UserName, request.Password,
                request.SaveToCredentialList && caller.IsAdmin, // the credential list is Administrator only
                owners.Own(request.RelatedSessionIds, caller), ct).ConfigureAwait(false);
            var message = Mappers.ToProto(found, false, 100, result);
            Mappers.AddRoutes(message, found, id => states?.NameOf(id));
            return message;
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    /// <summary>
    /// The devices of <paramref name="ids"/> that are still in <paramref name="view"/> (one removed or moved out meanwhile is
    /// left out); null when none is.
    /// </summary>
    internal static async Task<ViewedDevices?> StillInViewAsync(DeviceView view, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ids);
        try
        {
            return await view.GetAsync(ids, ct).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException)
        {
            var known = await view.IdsAsync(ct).ConfigureAwait(false);
            var rest = ids.Where(known.Contains).ToArray();
            try
            {
                return rest.Length == 0 ? null : await view.GetAsync(rest, ct).ConfigureAwait(false);
            }
            catch (DevicesNotAvailableException)
            {
                return null; // still moving: the polling's next round reads them
            }
        }
    }

    public override async Task<Proto.CommitReply> Commit(Proto.CommitRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var initialPassword = string.IsNullOrEmpty(request.InitialRootPassword) ? null : request.InitialRootPassword;
        try
        {
            foreach (var password in request.InitialPasswords.Values.Where(p => p.Length > 0).Append(initialPassword).OfType<string>())
            {
                VapixClient.ValidatePassword(password);
            }
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }

        var reach = await ReachAsync(context).ConfigureAwait(false);

        // One read of the managed serials for "managed already" and the limit (not one query per selected device).
        var managed = await adder.ManagedSerialsAsync(ct).ConfigureAwait(false);
        var resolved = ResolveCommit(request.SessionId, request.DiscoveredIds, reach, managed, context);

        // A user with a device group adds only devices that land inside the group (an excepted serial is not added).
        var selected = resolved.Where(f => reach.Takes(f.Serial, f.Route)).ToList();
        if (selected.Count < resolved.Count)
        {
            LogOutsideGroup(resolved.Count - selected.Count);
        }

        // The device limit for the whole add up front: nothing of an add that would go over it is stored.
        if (adder.WouldExceedLimit(managed, selected.Select(f => f.Serial)))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, DeviceLimits.TooManyDevicesText));
        }

        var serverSettings = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var serverName = serverSettings.ServerName;
        var useHostName = request.UseHostName; // the add page's "Use DNS names instead of IP addresses"
        var added = new ConcurrentDictionary<string, (Guid Id, SdkDeviceStatus Status)>();
        var limitHit = false;

        await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (found, token) =>
        {
            var deviceCredentials = PickCredentials(request.Credentials, found.DiscoveredId) ?? MatchedCredentials(request.SessionId, found);
            var devicePassword = request.InitialPasswords.TryGetValue(found.DiscoveredId, out var own) && own.Length > 0 ? own : initialPassword;
            var credentialsToStore = deviceCredentials is null ? null : new DeviceCredentials(deviceCredentials.UserName, deviceCredentials.Password);
            try
            {
                var result = await adder.AddAsync(found, useHostName, devicePassword, credentialsToStore, serverName, reach.AddedBy, token).ConfigureAwait(false);
                if (result is { } one)
                {
                    added[found.DiscoveredId] = one;
                }
            }
            catch (DeviceLimitException)
            {
                // Another add filled the server in the meantime: this device stays out, its result says Unknown.
                limitHit = true;
            }
        }).ConfigureAwait(false);

        var reply = new Proto.CommitReply();
        var ids = selected.Where(s => added.ContainsKey(s.DiscoveredId)).Select(s => added[s.DiscoveredId].Id).ToArray();
        reply.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        foreach (var found in resolved)
        {
            var ok = added.TryGetValue(found.DiscoveredId, out var one);
            reply.Results.Add(new Proto.CommitResult
            {
                DiscoveredId = found.DiscoveredId,
                DeviceId = ok ? one.Id.ToString() : string.Empty,
                Status = ok ? Mappers.ToProto(one.Status) : Proto.DeviceStatus.Unknown,
            });
        }

        if (ids.Length > 0)
        {
            // The first full refresh of the devices the caller just added (inside the caller's view by now); a device
            // removed meanwhile leaves the others in.
            if (await StillInViewAsync(await views.ForAsync(context).ConfigureAwait(false), ids, ct).ConfigureAwait(false) is { Count: > 0 } refresh)
            {
                actions.QueueRefresh(refresh);
            }

            var caller = AuthInterceptor.CallerOf(context);
            await MembershipAudit.AddedAsync(views, audit, caller.UserName, caller.ClientAddress, ids, string.Empty, ct, logger).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            authenticator.MarkAdded(request.SessionId, selected.Where(s => added.ContainsKey(s.DiscoveredId)).Select(s => s.Serial));
        }

        LogCommitted(ids.Length, selected.Count);
        if (limitHit && ids.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, DeviceLimits.TooManyDevicesText));
        }

        return reply;
    }

    /// <summary>The credential that worked in the session's automatic login or RetryAuth (server memory only).</summary>
    private Proto.DeviceCredentials? MatchedCredentials(string sessionId, DiscoveredDevice found)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || authenticator.MatchedCredentials(sessionId, found.Serial) is not { } matched)
        {
            return null;
        }

        return new Proto.DeviceCredentials { DiscoveredId = found.DiscoveredId, UserName = matched.UserName, Password = matched.Password };
    }

    /// <summary>Per-device credentials override the default entry (empty discovered_id).</summary>
    internal static Proto.DeviceCredentials? PickCredentials(IEnumerable<Proto.DeviceCredentials> all, string discoveredId)
    {
        var usable = all.Where(c => !string.IsNullOrWhiteSpace(c.UserName)).ToList();
        return usable.FirstOrDefault(c => string.Equals(c.DiscoveredId, discoveredId, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(c => string.IsNullOrEmpty(c.DiscoveredId));
    }

    /// <summary>The caller's reach; a caller who cannot add devices anywhere is refused before anything is read.</summary>
    private async Task<AddReach> ReachAsync(ServerCallContext context)
    {
        var reach = await AddReach.ForAsync(views, AuthInterceptor.CallerOf(context), context.CancellationToken).ConfigureAwait(false);
        return reach.CanAdd ? reach : throw new RpcException(new Status(StatusCode.PermissionDenied, AddDeviceTexts.CannotAdd));
    }

    /// <summary>
    /// Selected discovered devices of the caller's own session that are known, on a route of the caller and not managed yet,
    /// in request order. Another user's session, or a device on a route outside the caller's sites, is NOT_FOUND like an
    /// unknown one.
    /// </summary>
    private async Task<List<DiscoveredDevice>> ResolveAsync(string sessionId, IEnumerable<string> discoveredIds, AddReach reach, ServerCallContext context)
    {
        var managed = await adder.ManagedSerialsAsync(context.CancellationToken).ConfigureAwait(false);
        return ResolveCommit(sessionId, discoveredIds, reach, managed, context);
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> with the managed serials read once: the session's devices are looked up in one step and
    /// "managed already" is a set lookup, so a Commit of 30,000 devices is linear.
    /// </summary>
    private List<DiscoveredDevice> ResolveCommit(string sessionId, IEnumerable<string> discoveredIds, AddReach reach, IReadOnlySet<string> managed, ServerCallContext context)
    {
        var owned = owners.Owns(sessionId, AuthInterceptor.CallerOf(context));
        var ids = discoveredIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = owned ? discovery.FindDiscovered(sessionId, ids) : new Dictionary<string, DiscoveredDevice>();
        var result = new List<DiscoveredDevice>(ids.Count);
        foreach (var id in ids)
        {
            if (!known.TryGetValue(id, out var found) || !reach.Allows(found.Route))
            {
                throw GrpcGuard.NotFound($"Discovered device '{id}' is unknown (discovery session '{sessionId}').");
            }

            if (Equals(found.Address, IPAddress.None) || (DeviceSerial.TryNormalize(found.Serial, out var serial) && managed.Contains(serial)))
            {
                continue;
            }

            result.Add(found);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Add devices: {Added} of {Selected} device(s) added")]
    private partial void LogCommitted(int added, int selected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Add devices: {Count} device(s) not added, they would not be in the user's device group")]
    private partial void LogOutsideGroup(int count);
}
