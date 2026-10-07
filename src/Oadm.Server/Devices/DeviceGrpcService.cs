using Grpc.Core;

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
        await devices.RemoveManyAsync(GrpcGuard.ParseIds(request.Ids, "device id"), context.CancellationToken).ConfigureAwait(false);
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

    public override async Task<Proto.UrlReply> GetWebUiUrl(Proto.DeviceId request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "device id");
        var device = await devices.GetAsync(id, context.CancellationToken).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound($"Device {id} not found.");
        return new Proto.UrlReply { Url = Mappers.WebUiUrl(device) };
    }
}
