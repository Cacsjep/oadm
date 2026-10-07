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

    /// <summary>Snapshot (one ADDED per device), then live changes until the client or the server stops.</summary>
    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.DeviceChanged> responseStream, ServerCallContext context)
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

            await foreach (var change in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var hasCredentials = change.Kind != DeviceChangeKind.Removed
                    && await credentials.HasCredentialsAsync(change.DeviceId, ct).ConfigureAwait(false);
                await responseStream.WriteAsync(Mappers.ToProto(change, hasCredentials), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    public override async Task<Proto.Empty> Remove(Proto.DeviceIds request, ServerCallContext context)
    {
        foreach (var id in GrpcGuard.ParseIds(request.Ids, "device id"))
        {
            await devices.RemoveAsync(id, context.CancellationToken).ConfigureAwait(false);
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

        foreach (var id in ids)
        {
            try
            {
                await credentials.SetAsync(id, request.UserName.Trim(), request.Password, context.CancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                throw GrpcGuard.NotFound($"Device {id} not found.");
            }

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
