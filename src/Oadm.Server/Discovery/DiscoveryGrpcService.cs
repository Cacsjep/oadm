using System.Collections.Concurrent;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Server.AddDevices;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Discovery;

/// <summary>
/// gRPC DiscoveryService. WatchDiscovered sends one message per new or changed device
/// (<c>already_managed</c> from the device table) and again whenever the automatic login of the
/// <see cref="DiscoveryAuthenticator"/> changes its <c>auth_state</c>. Range scans and address
/// probes also send progress-only messages (empty <c>discovered_id</c>) and one with
/// <c>scan_finished</c> = true; their stream ends when every login of the session finished.
/// </summary>
public sealed class DiscoveryGrpcService(
    IDiscoveryBackend discovery,
    DeviceRepository devices,
    DiscoveryAuthenticator authenticator,
    IHostApplicationLifetime lifetime) : Proto.DiscoveryService.DiscoveryServiceBase
{
    public override async Task<Proto.DiscoverySession> StartZeroConf(Proto.Empty request, ServerCallContext context) =>
        new() { SessionId = (await discovery.StartZeroConfAsync(context.CancellationToken).ConfigureAwait(false)).Id };

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

    public override async Task<Proto.DiscoverySession> ProbeAddress(Proto.ProbeAddressRequest request, ServerCallContext context)
    {
        try
        {
            var session = await discovery.StartAddressProbeAsync(request.Address, context.CancellationToken).ConfigureAwait(false);
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
        var sessionId = request.SessionId;
        IAsyncEnumerable<DiscoveryEvent> events;
        try
        {
            events = discovery.WatchAsync(sessionId, ct);
        }
        catch (KeyNotFoundException)
        {
            throw GrpcGuard.NotFound($"Discovery session '{sessionId}' not found.");
        }

        // Discovery events and login results both produce messages; one writer sends them in order.
        var output = Channel.CreateUnbounded<Proto.DiscoveredDevice>(new UnboundedChannelOptions { SingleReader = true });
        var latest = new ConcurrentDictionary<string, (DiscoveredDevice Device, bool Managed, int Progress)>(StringComparer.OrdinalIgnoreCase);
        using var subscription = authenticator.Subscribe(sessionId, serial =>
        {
            if (latest.TryGetValue(serial, out var known))
            {
                output.Writer.TryWrite(Map(sessionId, known.Device, known.Managed, known.Progress));
            }
        });

        var pump = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var e in events.ConfigureAwait(false))
                    {
                        if (e.Device is { } device)
                        {
                            var managed = await devices.FindBySerialAsync(device.Serial, ct).ConfigureAwait(false) is not null;
                            latest[device.Serial] = (device, managed, e.ProgressPercent);
                            authenticator.Ensure(sessionId, device, managed);
                            var message = Map(sessionId, device, managed, e.ProgressPercent);
                            message.ScanFinished = e.Finished;
                            output.Writer.TryWrite(message);
                        }
                        else
                        {
                            output.Writer.TryWrite(Mappers.ToProgressProto(e.ProgressPercent, e.Finished));
                        }
                    }

                    // Scan finished or session stopped: the logins it started still report back.
                    await authenticator.WhenIdleAsync(sessionId, ct).ConfigureAwait(false);
                    output.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    output.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var message in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            await pump.ConfigureAwait(false);
        }
    }

    public override Task<Proto.Empty> StopScan(Proto.DiscoverySession request, ServerCallContext context)
    {
        discovery.StopScan(request.SessionId);
        return Task.FromResult(new Proto.Empty());
    }

    public override async Task<Proto.Empty> Stop(Proto.DiscoverySession request, ServerCallContext context)
    {
        await discovery.StopAsync(request.SessionId).ConfigureAwait(false);
        return new Proto.Empty();
    }

    private Proto.DiscoveredDevice Map(string sessionId, DiscoveredDevice device, bool managed, int progress) =>
        Mappers.ToProto(device, managed, progress, authenticator.Get(sessionId, device.Serial));
}
