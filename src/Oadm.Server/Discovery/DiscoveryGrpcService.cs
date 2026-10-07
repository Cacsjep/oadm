using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Discovery;

/// <summary>
/// gRPC DiscoveryService. WatchDiscovered sends one message per new or changed device
/// (<c>already_managed</c> from the device table). Range scans also send progress-only messages
/// (empty <c>discovered_id</c>) and a final one with <c>scan_finished</c> = true.
/// </summary>
public sealed class DiscoveryGrpcService(
    IDiscoveryBackend discovery,
    DeviceRepository devices,
    IHostApplicationLifetime lifetime) : Proto.DiscoveryService.DiscoveryServiceBase
{
    public override Task<Proto.DiscoverySession> StartZeroConf(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(new Proto.DiscoverySession { SessionId = discovery.StartZeroConf().Id });

    public override async Task<Proto.DiscoverySession> StartRangeScan(Proto.RangeScanRequest request, ServerCallContext context)
    {
        try
        {
            var session = await discovery.StartRangeScanAsync(request.From, request.To, context.CancellationToken).ConfigureAwait(false);
            return new Proto.DiscoverySession { SessionId = session.Id };
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task WatchDiscovered(Proto.DiscoverySession request, IServerStreamWriter<Proto.DiscoveredDevice> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        IAsyncEnumerable<Core.Discovery.DiscoveryEvent> events;
        try
        {
            events = discovery.WatchAsync(request.SessionId, ct);
        }
        catch (KeyNotFoundException)
        {
            throw GrpcGuard.NotFound($"Discovery session '{request.SessionId}' not found.");
        }

        try
        {
            await foreach (var e in events.ConfigureAwait(false))
            {
                Proto.DiscoveredDevice message;
                if (e.Device is { } device)
                {
                    var managed = await devices.FindBySerialAsync(device.Serial, ct).ConfigureAwait(false) is not null;
                    message = Mappers.ToProto(device, managed, e.ProgressPercent);
                    message.ScanFinished = e.Finished;
                }
                else
                {
                    message = Mappers.ToProgressProto(e.ProgressPercent, e.Finished);
                }

                await responseStream.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
    }

    public override async Task<Proto.Empty> Stop(Proto.DiscoverySession request, ServerCallContext context)
    {
        await discovery.StopAsync(request.SessionId).ConfigureAwait(false);
        return new Proto.Empty();
    }
}
