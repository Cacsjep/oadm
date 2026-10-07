using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;
using Oadm.Server.Common;
using Oadm.Sdk.Devices;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.LiveView;

/// <summary>
/// gRPC LiveViewService: relays encoded access units of a device's camera stream. The camera
/// connection (RTSP with the stored credentials) is owned by the <see cref="LiveViewHub"/> and
/// shared between viewers; this call only reads its viewer queue.
/// </summary>
public sealed class LiveViewGrpcService(
    DeviceRepository devices,
    LiveViewHub hub,
    ILiveVideoSourceFactory sources,
    IHostApplicationLifetime lifetime) : Proto.LiveViewService.LiveViewServiceBase
{
    /// <summary>Video sources (view areas, sensors, encoder channels) of a device. Read-only on the camera.</summary>
    public override async Task<Proto.LiveViewSources> ListSources(Proto.LiveViewSourcesRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.DeviceId, "device id");
        _ = await devices.GetAsync(id, context.CancellationToken).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound($"Device {id} not found.");
        LiveViewCapabilities capabilities;
        try
        {
            capabilities = await sources.GetCapabilitiesAsync(id, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToRpc(ex);
        }

        var reply = new Proto.LiveViewSources();
        foreach (var source in capabilities.EffectiveSources)
        {
            var largest = source.Resolutions.Count > 0 ? source.Resolutions.MaxBy(r => (long)r.Width * r.Height) : default;
            reply.Sources.Add(new Proto.LiveViewSource
            {
                Camera = source.Camera,
                Name = source.Name,
                Sensor = source.Sensor,
                MaxWidth = largest.Width,
                MaxHeight = largest.Height,
            });
        }

        return reply;
    }

    public override async Task Watch(Proto.LiveViewRequest request, IServerStreamWriter<Proto.LiveViewFrame> responseStream, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.DeviceId, "device id");

        // Encoded video does not compress; skip the server-wide gzip for these frames.
        context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var device = await devices.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound($"Device {id} not found.");
        if (device.Status == DeviceStatus.CertificateChanged)
        {
            throw GrpcGuard.FailedPrecondition("The device certificate changed; accept the new certificate first.");
        }

        var codecs = request.AcceptedCodecs.Select(FromProto).OfType<VideoCodecKind>().ToList();
        LiveViewSubscription subscription;
        try
        {
            subscription = await hub.SubscribeAsync(id, request.MaxWidth, request.MaxHeight, request.Fps, codecs, ct, request.Camera).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToRpc(ex);
        }

        await using (subscription.ConfigureAwait(false))
        {
            try
            {
                await foreach (var frame in subscription.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var message = ToProto(frame);
                    message.Camera = subscription.Key.Camera;
                    await responseStream.WriteAsync(message, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Viewer closed the panel or the server is stopping.
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
            {
                throw ToRpc(ex);
            }
        }
    }

    internal static Proto.LiveViewFrame ToProto(LiveVideoFrame frame) => new()
    {
        Codec = ToProto(frame.Codec),
        Keyframe = frame.IsKeyframe,
        RtpTimestamp = frame.RtpTimestamp,
        Data = ByteString.CopyFrom(frame.Data),
        CodecConfig = frame.CodecConfig is null ? ByteString.Empty : ByteString.CopyFrom(frame.CodecConfig),
        Width = frame.Width,
        Height = frame.Height,
        Captured = Timestamp.FromDateTimeOffset(frame.Captured),
    };

    internal static Proto.VideoCodec ToProto(VideoCodecKind codec) => codec switch
    {
        VideoCodecKind.H265 => Proto.VideoCodec.H265,
        _ => Proto.VideoCodec.H264,
    };

    internal static VideoCodecKind? FromProto(Proto.VideoCodec codec) => codec switch
    {
        Proto.VideoCodec.H264 => VideoCodecKind.H264,
        Proto.VideoCodec.H265 => VideoCodecKind.H265,
        _ => null,
    };

    private static RpcException ToRpc(Exception ex) => ex switch
    {
        RpcException rpc => rpc,
        KeyNotFoundException => GrpcGuard.NotFound(ex.Message),
        LiveViewException { Error: LiveViewError.Unauthorized } => new RpcException(new Status(StatusCode.PermissionDenied, ex.Message)),
        LiveViewException { Error: LiveViewError.NotSupported } => GrpcGuard.FailedPrecondition(ex.Message),
        VapixAuthenticationException => new RpcException(new Status(StatusCode.PermissionDenied, "The camera rejected the stored credentials.")),
        CertificateChangedException => GrpcGuard.FailedPrecondition("The device certificate changed; accept the new certificate first."),
        _ => new RpcException(new Status(StatusCode.Unavailable, ex.Message)),
    };
}
