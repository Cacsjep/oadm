using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;
using Oadm.Server.Common;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.LiveView;

/// <summary>
/// gRPC LiveViewService: relays encoded access units of a device's camera stream. The camera
/// connection (RTSP with the stored credentials) is owned by the <see cref="LiveViewHub"/> and
/// shared between viewers; this call only reads its viewer queue. Also the camera's zoom and focus
/// (<see cref="IDeviceOptics"/>) and the RTSP port OADM uses per device (stored in OADM only).
/// </summary>
public sealed class LiveViewGrpcService(
    DeviceRepository devices,
    LiveViewHub hub,
    ILiveVideoSourceFactory sources,
    IHostApplicationLifetime lifetime,
    IDeviceOptics optics,
    IDevicePtz ptz,
    IDeviceEventStreams ports,
    AuditLog audit) : Proto.LiveViewService.LiveViewServiceBase
{
    /// <summary>Most RTSP port requests name one device; a selection is capped like other device lists.</summary>
    private const int MaxPortDevices = 5000;

    /// <summary>Zoom and focus of a device. Read-only; a device without them, or one OADM cannot log in to, is "not supported".</summary>
    public override async Task<Proto.OpticsState> GetOptics(Proto.OpticsRequest request, ServerCallContext context)
    {
        var device = await RequireDeviceAsync(request.DeviceId, context.CancellationToken).ConfigureAwait(false);
        if (TaskSupportReasons.ForStatus(device.Status) is not null)
        {
            return new Proto.OpticsState();
        }

        try
        {
            return ToProto(await optics.GetAsync(device.Id, Math.Max(0, request.Sensor), context.CancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is VapixException or HttpRequestException or IOException or TaskCanceledException && !context.CancellationToken.IsCancellationRequested)
        {
            // Optics are an extra of the live view: a camera that cannot answer simply shows no controls.
            return new Proto.OpticsState();
        }
    }

    /// <summary>One zoom or focus change ("Optics Control"); audited, the reply is the state read again.</summary>
    public override async Task<Proto.OpticsState> SetOptics(Proto.SetOpticsRequest request, ServerCallContext context)
    {
        var device = await RequireDeviceAsync(request.DeviceId, context.CancellationToken).ConfigureAwait(false);
        if (TaskSupportReasons.ForStatus(device.Status) is { } refused)
        {
            throw GrpcGuard.FailedPrecondition(refused + ".");
        }

        var change = new OpticsChange
        {
            Magnification = request.HasMagnification ? request.Magnification : null,
            FocusPosition = request.HasFocusPosition ? request.FocusPosition : null,
            FocusWindow = request.FocusWindow is { } w ? new OpticsWindow(w.X, w.Y, w.Width, w.Height) : null,
            Autofocus = request.Autofocus,
            ResetFocus = request.ResetFocus,
        };
        int chosen = (change.Magnification is null ? 0 : 1) + (change.FocusPosition is null ? 0 : 1) + (change.FocusWindow is null ? 0 : 1)
            + (change.Autofocus ? 1 : 0) + (change.ResetFocus ? 1 : 0);
        if (chosen != 1)
        {
            throw GrpcGuard.InvalidArgument("Set exactly one of zoom, focus, autofocus area, autofocus or reset focus.");
        }

        if (change.Magnification is { } z && !double.IsFinite(z) || change.FocusPosition is { } f && !double.IsFinite(f)
            || change.FocusWindow is { } a && !(double.IsFinite(a.X) && double.IsFinite(a.Y) && double.IsFinite(a.Width) && double.IsFinite(a.Height)))
        {
            throw GrpcGuard.InvalidArgument("The value is not a number.");
        }

        Core.Vapix.OpticsState state;
        try
        {
            state = await optics.SetAsync(device.Id, Math.Max(0, request.Sensor), change, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OpticsException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.UserMessage);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw ToRpc(ex);
        }

        await audit.WriteAsync(AuditActions.OpticsChanged, $"{device.Address} ({device.Serial})", change.Describe(), context.CancellationToken).ConfigureAwait(false);
        return ToProto(state);
    }

    /// <summary>What a PTZ camera offers on a channel. Read-only; a camera without PTZ, or one OADM cannot log in to, is "not supported".</summary>
    public override async Task<Proto.PtzState> GetPtz(Proto.PtzRequest request, ServerCallContext context)
    {
        var device = await RequireDeviceAsync(request.DeviceId, context.CancellationToken).ConfigureAwait(false);
        if (TaskSupportReasons.ForStatus(device.Status) is not null)
        {
            return new Proto.PtzState();
        }

        try
        {
            var state = await ptz.GetAsync(device.Id, Math.Max(1, request.Camera), context.CancellationToken).ConfigureAwait(false);
            var reply = new Proto.PtzState
            {
                Supported = state.IsSupported,
                CanMove = state.CanMove,
                CanZoom = state.CanZoom,
                CanCenter = state.CanCenter,
                CanGoHome = state.CanGoHome,
                Zoom = state.Zoom,
            };
            reply.Presets.AddRange(state.Presets);
            return reply;
        }
        catch (Exception ex) when (ex is VapixException or HttpRequestException or IOException or TaskCanceledException && !context.CancellationToken.IsCancellationRequested)
        {
            return new Proto.PtzState();
        }
    }

    /// <summary>One PTZ command ("Optics Control"). Not audited: a drag sends several moves a second.</summary>
    public override async Task<Proto.Empty> MovePtz(Proto.MovePtzRequest request, ServerCallContext context)
    {
        var device = await RequireDeviceAsync(request.DeviceId, context.CancellationToken).ConfigureAwait(false);
        if (TaskSupportReasons.ForStatus(device.Status) is { } refused)
        {
            throw GrpcGuard.FailedPrecondition(refused + ".");
        }

        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
        {
            throw GrpcGuard.InvalidArgument("The value is not a number.");
        }

        var kind = request.Move switch
        {
            Proto.PtzMove.Continuous => PtzCommandKind.Move,
            Proto.PtzMove.Stop => PtzCommandKind.Stop,
            Proto.PtzMove.Zoom => PtzCommandKind.Zoom,
            Proto.PtzMove.ZoomBy => PtzCommandKind.ZoomBy,
            Proto.PtzMove.Center => PtzCommandKind.Center,
            Proto.PtzMove.Home => PtzCommandKind.Home,
            Proto.PtzMove.Preset => PtzCommandKind.Preset,
            _ => throw GrpcGuard.InvalidArgument("Choose a PTZ command."),
        };
        try
        {
            await ptz.SendAsync(device.Id, Math.Max(1, request.Camera), new PtzCommand(kind, request.X, request.Y, request.Value, request.Preset), context.CancellationToken).ConfigureAwait(false);
        }
        catch (PtzException ex)
        {
            throw GrpcGuard.FailedPrecondition("The camera refused: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw ToRpc(ex);
        }

        return new Proto.Empty();
    }

    /// <summary>The RTSP port the devices share (554 = default) and whether they differ.</summary>
    public override async Task<Proto.RtspPortReply> GetRtspPort(Proto.RtspPortRequest request, ServerCallContext context)
    {
        var ids = PortDevices(request.DeviceIds);
        var found = new HashSet<int>();
        foreach (var id in ids)
        {
            found.Add(await ports.GetRtspPortAsync(id, context.CancellationToken).ConfigureAwait(false) ?? RtspClient.DefaultPort);
        }

        return new Proto.RtspPortReply { Port = found.Count == 1 ? found.First() : RtspClient.DefaultPort, Mixed = found.Count > 1 };
    }

    /// <summary>Stores the RTSP port of the devices in OADM ("Optics Control"; the devices are not changed); audited.</summary>
    public override async Task<Proto.RtspPortReply> SetRtspPort(Proto.SetRtspPortRequest request, ServerCallContext context)
    {
        var ids = PortDevices(request.DeviceIds);
        if (request.Port is < 0 or > 65535)
        {
            throw GrpcGuard.InvalidArgument("The RTSP port must be from 1 to 65535.");
        }

        int? port = request.Port is 0 or RtspClient.DefaultPort ? null : request.Port;
        var known = new List<Device>();
        foreach (var id in ids)
        {
            if (await devices.GetAsync(id, context.CancellationToken).ConfigureAwait(false) is { } device)
            {
                known.Add(device);
            }
        }

        if (known.Count == 0)
        {
            throw GrpcGuard.NotFound("The devices were not found.");
        }

        foreach (var device in known)
        {
            await ports.SetRtspPortAsync(device.Id, port, context.CancellationToken).ConfigureAwait(false);
        }

        var target = known.Count == 1 ? $"{known[0].Address} ({known[0].Serial})" : $"{known.Count} devices";
        await audit.WriteAsync(AuditActions.RtspPortChanged, target, port is null ? "554 (default)" : port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), context.CancellationToken).ConfigureAwait(false);
        return new Proto.RtspPortReply { Port = port ?? RtspClient.DefaultPort };
    }

    private static List<Guid> PortDevices(IEnumerable<string> raw)
    {
        var ids = GrpcGuard.ParseIds(raw, "device id").Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxPortDevices)
        {
            throw GrpcGuard.InvalidArgument($"Name 1 to {MaxPortDevices} devices.");
        }

        return ids;
    }

    private async Task<Device> RequireDeviceAsync(string rawId, CancellationToken ct)
    {
        var id = GrpcGuard.ParseId(rawId, "device id");
        return await devices.GetAsync(id, ct).ConfigureAwait(false) ?? throw GrpcGuard.NotFound($"Device {id} not found.");
    }

    internal static Proto.OpticsState ToProto(Core.Vapix.OpticsState state)
    {
        var reply = new Proto.OpticsState
        {
            Supported = state.IsSupported,
            HasZoom = state.HasZoom,
            HasFocus = state.HasFocus,
            MaxMagnification = state.MaxMagnification,
            Magnification = state.Magnification,
            FocusPosition = state.FocusPosition,
            ZoomMoving = state.ZoomMoving,
            FocusMoving = state.FocusMoving,
        };
        if (state.FocusWindow is { } w)
        {
            reply.FocusWindow = new Proto.OpticsWindow { X = w.X, Y = w.Y, Width = w.Width, Height = w.Height };
        }

        return reply;
    }

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
            throw GrpcGuard.FailedPrecondition(DeviceMessages.CertificateChanged);
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
        CertificateChangedException => GrpcGuard.FailedPrecondition(DeviceMessages.CertificateChanged),
        _ => new RpcException(new Status(StatusCode.Unavailable, ex.Message)),
    };
}
