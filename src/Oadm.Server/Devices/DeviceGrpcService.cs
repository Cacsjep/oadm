using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// gRPC DeviceService: the device table, its live change stream and per-device actions. Every call gets its devices from
/// the caller's <see cref="DeviceView"/> (docs/spec/device-scope.md): the list and the feed carry only devices of the view,
/// and a request with any id that is unknown or outside the view is refused whole (NOT_FOUND "N devices are no longer
/// available.", the same answer for every user). The actions run through <see cref="DeviceActions"/>, which takes only
/// viewed devices.
/// </summary>
public sealed class DeviceGrpcService(
    DeviceActions actions,
    CredentialListStore credentialList,
    AuditLog audit,
    IHostApplicationLifetime lifetime,
    DeviceViewFactory views,
    Core.Vapix.TrustAnchorRegistry anchors) : Proto.DeviceService.DeviceServiceBase
{
    /// <summary>Devices read from the database at a time for a device list.</summary>
    public const int SnapshotPageSize = 1_000;

    /// <summary>Changes taken from the feed at a time.</summary>
    private const int ChangeBatchSize = 1_000;

    /// <summary>The device with its status note (kept in server memory, <see cref="DeviceStatusNotes"/>).</summary>
    private Proto.Device ToProto(Device device, bool hasCredentials) => WithNote(Mappers.ToProto(device, hasCredentials), device);

    private Proto.DeviceChanged ToProto(DeviceChange change, bool hasCredentials)
    {
        var proto = Mappers.ToProto(change, hasCredentials);
        if (change.Kind != DeviceChangeKind.Removed && change.Device is { } device)
        {
            WithNote(proto.Device, device);
        }

        return proto;
    }

    private Proto.Device WithNote(Proto.Device proto, Device device)
    {
        proto.CertWarningDays = anchors.ExpiryWarningDays(device); // PKI's Expiry warning policy of the device
        if (proto.Status == Proto.DeviceStatus.CredentialsRequired && actions.StatusNote(device) is { } note)
        {
            proto.StatusDetail = note;
        }

        return proto;
    }

    /// <summary>Every device of the caller's view in one message (legacy; clients use Watch).</summary>
    public override async Task<Proto.DeviceList> List(Proto.Empty request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var withCredentials = await actions.DeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false);
        var reply = new Proto.DeviceList();
        await foreach (var page in view.PagesAsync(SnapshotPageSize, ct).ConfigureAwait(false))
        {
            reply.Devices.AddRange(page.Select(device => ToProto(device, withCredentials.Contains(device.Id))));
        }

        return reply;
    }

    /// <summary>
    /// Snapshot (one ADDED per device of the caller's view), SNAPSHOT_END when asked for, then live changes until the client
    /// or the server stops. A slow client gets the changes coalesced per device, never dropped. A device that leaves the
    /// view becomes REMOVED, one that enters it ADDED (<see cref="DeviceViewFeed"/>); a change of the caller's group ends the
    /// stream (<see cref="CallerStreams"/>) and the client connects again with the new view.
    /// </summary>
    public override async Task Watch(Proto.WatchDevicesRequest request, IServerStreamWriter<Proto.DeviceChanged> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var view = await views.ForAsync(context).ConfigureAwait(false);

        // Subscribe first, then snapshot: nothing that happens in between is lost.
        using var feed = view.Subscribe();
        try
        {
            // In pages: one client's snapshot holds at most SnapshotPageSize devices in memory.
            var withCredentials = await actions.DeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false);
            await foreach (var page in view.PagesAsync(SnapshotPageSize, ct).ConfigureAwait(false))
            {
                foreach (var device in page)
                {
                    feed.Sent(device.Id);
                    var change = new DeviceChange(DeviceChangeKind.Added, device.Id, device);
                    await responseStream.WriteAsync(ToProto(change, withCredentials.Contains(device.Id)), ct).ConfigureAwait(false);
                }
            }

            if (request.SnapshotEndMarker)
            {
                await responseStream.WriteAsync(new Proto.DeviceChanged { Kind = Proto.DeviceChanged.Types.Kind.SnapshotEnd }, ct).ConfigureAwait(false);
            }

            // Changes are taken in batches: a burst (Refresh all, a Run on 5,000 devices) asks the
            // credential table once per batch instead of once per device.
            while (await feed.ReadAsync(ChangeBatchSize, ct).ConfigureAwait(false) is { } batch)
            {
                var withCredentialsNow = batch.Count > 16
                    ? await actions.DeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false)
                    : null;
                foreach (var change in batch)
                {
                    var hasCredentials = change.Kind != DeviceChangeKind.Removed && change.Device is { } device
                        && (withCredentialsNow?.Contains(change.DeviceId)
                            ?? await actions.HasCredentialsAsync(device, ct).ConfigureAwait(false));
                    await responseStream.WriteAsync(ToProto(change, hasCredentials), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    /// <summary>Removes the devices in one transaction. Any id that is unknown or outside the view refuses the whole request.</summary>
    public override async Task<Proto.Empty> Remove(Proto.DeviceIds request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, ct).ConfigureAwait(false);
        var removed = (await actions.RemoveAsync(viewed, ct).ConfigureAwait(false)).ToHashSet();
        var addresses = viewed.Where(d => removed.Contains(d.Id)).Select(d => d.Address).ToList();
        if (addresses.Count > 0)
        {
            var target = string.Join(", ", addresses.Take(5)) + (addresses.Count > 5 ? $" +{addresses.Count - 5} more" : string.Empty);
            await audit.WriteAsync(AuditActions.DevicesRemoved, target, addresses.Count == 1 ? "1 device" : $"{addresses.Count} devices", ct).ConfigureAwait(false);
        }

        return new Proto.Empty();
    }

    /// <summary>
    /// Queues a full refresh; results arrive through Watch. No ids: every device of the caller's view. Any id that is unknown
    /// or outside the view refuses the whole request.
    /// </summary>
    public override async Task<Proto.Empty> Refresh(Proto.DeviceIds request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetOrAllAsync(ids, context.CancellationToken).ConfigureAwait(false);
        actions.QueueRefresh(viewed);
        return new Proto.Empty();
    }

    public override async Task<Proto.Empty> SetCredentials(Proto.SetCredentialsRequest request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            throw GrpcGuard.InvalidArgument("User name is required.");
        }

        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, context.CancellationToken).ConfigureAwait(false);
        try
        {
            // One transaction for all devices (5,000 selected devices: one commit, not 5,000).
            await actions.SetCredentialsAsync(viewed, request.UserName.Trim(), request.Password, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Removed between the check and the write: nothing was stored, the answer of any device that is gone.
            throw GrpcGuard.NotFound(DeviceMessages.NoLongerAvailable(1));
        }

        return new Proto.Empty();
    }

    /// <summary>
    /// "Log in" (device context menu) for devices whose stored credentials are rejected: tries the credential on every
    /// device (bounded parallelism, one call for the selection), stores it for those that accept it and queues their
    /// refresh. "Save to credential list" only for administrators; a full list is a note, never a failed login.
    /// </summary>
    public override async Task<Proto.DeviceLogInReply> LogIn(Proto.DeviceLogInRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        if (ids.Length == 0)
        {
            throw GrpcGuard.InvalidArgument("Select at least one device.");
        }

        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            throw GrpcGuard.InvalidArgument("Enter a user name.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            throw GrpcGuard.InvalidArgument("Enter a password.");
        }

        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, ct).ConfigureAwait(false);
        var results = await actions.LogInAsync(viewed, request.UserName, request.Password, ct).ConfigureAwait(false);
        var reply = new Proto.DeviceLogInReply();
        foreach (var result in results)
        {
            reply.Results.Add(new Proto.DeviceLogInResult
            {
                DeviceId = result.DeviceId.ToString(),
                Ok = result.Ok,
                Rejected = result.Rejected,
                Message = result.Message ?? string.Empty,
            });
        }

        var okCount = results.Count(r => r.Ok);

        // The credential list is Admin only: an operator's request to save is ignored (the client hides the option).
        if (request.SaveToCredentialList && okCount > 0 && AuthInterceptor.CallerOf(context).IsAdmin)
        {
            try
            {
                var added = await credentialList.AddAsync(request.UserName, request.Password, ct).ConfigureAwait(false);
                await audit.WriteAsync(AuditActions.CredentialAdded, added.UserName, null, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                reply.CredentialListNote = $"The credential list is full ({CredentialListStore.MaxEntries} entries); the login was not saved there.";
            }
            catch (ArgumentException ex)
            {
                reply.CredentialListNote = "The login was not saved in the credential list: " + ex.Message;
            }
        }

        var devicesText = results.Count == 1 ? "1 device" : $"{results.Count} devices";
        await audit.WriteAsync(AuditActions.DeviceLogin, request.UserName.Trim(), $"{devicesText}, {okCount} logged in", ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>The user name stored for all of these devices when they share one (prefill of the Log in dialog).</summary>
    public override async Task<Proto.CredentialUserNameReply> GetCredentialUserName(Proto.DeviceIds request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, context.CancellationToken).ConfigureAwait(false);
        var name = await actions.SharedUserNameAsync(viewed, context.CancellationToken).ConfigureAwait(false);
        return new Proto.CredentialUserNameReply { UserName = name ?? string.Empty };
    }

    /// <summary>
    /// "Set password" (device context menu) for devices in factory default: the first root password on every device
    /// (bounded parallelism, needsetup re-checked right before writing), stored for those that took it. Never logged.
    /// </summary>
    public override async Task<Proto.SetFirstPasswordReply> SetFirstPassword(Proto.SetFirstPasswordRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        if (ids.Length == 0)
        {
            throw GrpcGuard.InvalidArgument("Select at least one device.");
        }

        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, ct).ConfigureAwait(false);
        IReadOnlyList<DeviceFirstPasswordResult> results;
        try
        {
            results = await actions.SetFirstPasswordAsync(viewed, request.Password ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message.Split(" (Parameter", 2)[0]);
        }

        var reply = new Proto.SetFirstPasswordReply();
        foreach (var result in results)
        {
            reply.Results.Add(new Proto.SetFirstPasswordResult { DeviceId = result.DeviceId.ToString(), Ok = result.Ok, Message = result.Message ?? string.Empty });
        }

        var okCount = results.Count(r => r.Ok);
        var devicesText = results.Count == 1 ? "1 device" : $"{results.Count} devices";
        await audit.WriteAsync(AuditActions.FirstPasswordSet, "root", $"{devicesText}, {okCount} set", ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>The passphrase policy each device reports (anonymous systemready), for the hint of the Set password dialog.</summary>
    public override async Task<Proto.PassphrasePoliciesReply> GetPassphrasePolicies(Proto.DeviceIds request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        var reply = new Proto.PassphrasePoliciesReply();
        // Only devices of the caller's view; any other id refuses the whole request (NOT_FOUND) before a device is asked.
        var viewed = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(ids, context.CancellationToken).ConfigureAwait(false);
        foreach (var policy in await actions.ReadPoliciesAsync(viewed, context.CancellationToken).ConfigureAwait(false))
        {
            reply.Policies.Add(new Proto.DevicePassphrasePolicy { DeviceId = policy.DeviceId.ToString(), Policy = policy.Policy ?? string.Empty });
        }

        return reply;
    }

    public override async Task<Proto.UrlReply> GetWebUiUrl(Proto.DeviceId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "device id");
        var device = await (await views.ForAsync(context).ConfigureAwait(false)).GetAsync(id, context.CancellationToken).ConfigureAwait(false);
        return new Proto.UrlReply { Url = Mappers.WebUiUrl(device) };
    }
}
