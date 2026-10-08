using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>gRPC DeviceService: the device table, its live change stream and per-device actions.</summary>
public sealed class DeviceGrpcService(
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    DevicePollingService polling,
    DeviceLoginService logins,
    CredentialListStore credentialList,
    AuditLog audit,
    IHostApplicationLifetime lifetime) : Proto.DeviceService.DeviceServiceBase
{
    public override async Task<Proto.DeviceList> List(Proto.Empty request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var withCredentials = await credentials.ListDeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false);
        var reply = new Proto.DeviceList();
        foreach (var device in await devices.ListDevicesAsync(ct).ConfigureAwait(false))
        {
            reply.Devices.Add(Mappers.ToProto(device, withCredentials.Contains(device.Id)));
        }

        return reply;
    }

    /// <summary>
    /// Snapshot (one ADDED per device), SNAPSHOT_END when asked for, then live changes until the client
    /// or the server stops. A slow client gets the changes coalesced per device, never dropped.
    /// </summary>
    public override async Task Watch(Proto.WatchDevicesRequest request, IServerStreamWriter<Proto.DeviceChanged> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;

        // Subscribe first, then snapshot: nothing that happens in between is lost.
        using var subscription = devices.Changes.Subscribe();
        try
        {
            var withCredentials = await credentials.ListDeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false);
            foreach (var device in await devices.ListDevicesAsync(ct).ConfigureAwait(false))
            {
                var change = new DeviceChange(DeviceChangeKind.Added, device.Id, device);
                await responseStream.WriteAsync(Mappers.ToProto(change, withCredentials.Contains(device.Id)), ct).ConfigureAwait(false);
            }

            if (request.SnapshotEndMarker)
            {
                await responseStream.WriteAsync(new Proto.DeviceChanged { Kind = Proto.DeviceChanged.Types.Kind.SnapshotEnd }, ct).ConfigureAwait(false);
            }

            // Changes are taken in batches: a burst (Refresh all, a Run on 5,000 devices) asks the
            // credential table once per batch instead of once per device.
            var reader = subscription.Reader;
            var batch = new List<DeviceChange>();
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 1000 && reader.TryRead(out var next))
                {
                    batch.Add(next);
                }

                var withCredentialsNow = batch.Count > 16
                    ? await credentials.ListDeviceIdsWithCredentialsAsync(ct).ConfigureAwait(false)
                    : null;
                foreach (var change in batch)
                {
                    var hasCredentials = change.Kind != DeviceChangeKind.Removed
                        && (withCredentialsNow?.Contains(change.DeviceId)
                            ?? await credentials.HasCredentialsAsync(change.DeviceId, ct).ConfigureAwait(false));
                    await responseStream.WriteAsync(Mappers.ToProto(change, hasCredentials), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    /// <summary>Removes the devices in one transaction (unknown ids are ignored).</summary>
    public override async Task<Proto.Empty> Remove(Proto.DeviceIds request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        var wanted = ids.ToHashSet();
        var addresses = (await devices.ListDevicesAsync(context.CancellationToken).ConfigureAwait(false))
            .Where(d => wanted.Contains(d.Id)).Select(d => d.Address).ToList();
        await devices.RemoveManyAsync(ids, context.CancellationToken).ConfigureAwait(false);
        if (addresses.Count > 0)
        {
            var target = string.Join(", ", addresses.Take(5)) + (addresses.Count > 5 ? $" +{addresses.Count - 5} more" : string.Empty);
            await audit.WriteAsync(AuditActions.DevicesRemoved, target, addresses.Count == 1 ? "1 device" : $"{addresses.Count} devices", context.CancellationToken).ConfigureAwait(false);
        }

        return new Proto.Empty();
    }

    /// <summary>Queues a full refresh; results arrive through Watch.</summary>
    public override async Task<Proto.Empty> Refresh(Proto.DeviceIds request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.Ids, "device id");
        if (ids.Length == 0)
        {
            ids = [.. (await devices.ListDevicesAsync(context.CancellationToken).ConfigureAwait(false)).Select(d => d.Id)];
        }

        polling.QueueRefresh(ids);
        return new Proto.Empty();
    }

    public override async Task<Proto.Empty> SetCredentials(Proto.SetCredentialsRequest request, ServerCallContext context)
    {
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            throw GrpcGuard.InvalidArgument("User name is required.");
        }

        try
        {
            // One transaction for all devices (5,000 selected devices: one commit, not 5,000).
            await credentials.SetManyAsync(ids, request.UserName.Trim(), request.Password, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }

        foreach (var id in ids)
        {
            clients.Invalidate(id);
        }

        polling.QueueRefresh(ids);
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

        var results = await logins.LogInAsync(ids, request.UserName, request.Password, ct).ConfigureAwait(false);
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
        if (request.SaveToCredentialList && okCount > 0 && CallerContext.Current is not { IsAdmin: false })
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
        var name = await logins.SharedUserNameAsync(ids, context.CancellationToken).ConfigureAwait(false);
        return new Proto.CredentialUserNameReply { UserName = name ?? string.Empty };
    }

    public override async Task<Proto.UrlReply> GetWebUiUrl(Proto.DeviceId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "device id");
        var device = await devices.GetAsync(id, context.CancellationToken).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound($"Device {id} not found.");
        return new Proto.UrlReply { Url = Mappers.WebUiUrl(device) };
    }
}
