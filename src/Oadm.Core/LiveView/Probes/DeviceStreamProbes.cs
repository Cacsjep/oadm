using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.LiveView.Probes;

/// <summary>
/// Server implementation of <see cref="IDeviceStreamProbes"/> (docs/spec/live-view.md "Stream probes"): the video sources
/// from param.cgi (<c>Properties.Image</c>, <c>Image</c>, like the live view) with the frame rate of the sensor's capture
/// mode, and measuring streams (<see cref="RtspStreamProbe"/>) with the stored credentials, which never leave the server,
/// on the device's RTSP port and route. Not shared with the live view: every probe is its own camera connection.
/// </summary>
public sealed class DeviceStreamProbes(
    DeviceRepository devices,
    CredentialStore credentials,
    IVapixClientFactory clients,
    ILoggerFactory? loggerFactory = null,
    IDeviceEventStreams? ports = null,
    DeviceRouter? router = null,
    BasicOverHttpSetting? basicOverHttp = null,
    RtpUdpPorts? udpPorts = null) : IDeviceStreamProbes
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>The UDP port pairs of every probe of this server.</summary>
    public RtpUdpPorts UdpPorts { get; } = udpPorts ?? new RtpUdpPorts();

    /// <summary>RTSP port of the devices without a stored port (tests use a loopback server).</summary>
    public int RtspPort { get; init; } = RtspClient.DefaultPort;

    /// <summary>Clock of the probes (tests: a fake one).</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>Binds the probes' UDP ports (tests); default <see cref="DeviceRouter.BindUdp"/>.</summary>
    public Func<DeviceRoute, System.Net.Sockets.AddressFamily, int, IUdpPort?>? BindUdp { get; init; }

    public async Task<IReadOnlyList<StreamProbeSource>> ListSourcesAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await RequireAsync(deviceId, ct).ConfigureAwait(false);
        return await ReadSourcesAsync(device.Id, ct).ConfigureAwait(false);
    }

    public async Task<IDeviceStreamProbe> OpenAsync(Guid deviceId, StreamProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var codec = request.Codec switch
        {
            null => (VideoCodecKind?)null,
            _ when request.Codec.Equals("h265", StringComparison.OrdinalIgnoreCase) => VideoCodecKind.H265,
            _ when request.Codec.Equals("h264", StringComparison.OrdinalIgnoreCase) => VideoCodecKind.H264,
            _ => throw new ArgumentException($"Unknown codec '{request.Codec}': h265 or h264.", nameof(request)),
        };
        var device = await RequireAsync(deviceId, ct).ConfigureAwait(false);

        var width = request.Width;
        var height = request.Height;
        var fps = request.Fps;
        if (width <= 0 || height <= 0 || fps <= 0 || codec is null)
        {
            var sources = await ReadSourcesAsync(device.Id, ct).ConfigureAwait(false);
            var source = sources.FirstOrDefault(s => s.Camera == Math.Max(1, request.Camera))
                ?? throw new DeviceStreamException(DeviceStreamError.NotSupported, RtspStreamProbe.NoVideoStream);
            if (width <= 0 || height <= 0)
            {
                (width, height) = (source.Width, source.Height);
            }

            fps = fps > 0 ? fps : source.MaxFps;
            codec ??= source.Codecs.Contains("h265") ? VideoCodecKind.H265 : VideoCodecKind.H264;
        }

        var stored = await credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        var port = ports is null ? null : await ports.GetRtspPortAsync(device.Id, ct).ConfigureAwait(false);
        return await RtspStreamProbe.OpenAsync(
            new RtspProbeOptions
            {
                Address = device.Address,
                Port = port ?? RtspPort,
                Credentials = stored is null ? null : new NetworkCredential(stored.UserName, stored.Password),
                AllowBasic = basicOverHttp?.Current ?? false,
                BasicAllowed = basicOverHttp is null ? null : () => basicOverHttp.Current,
                Codec = codec.Value,
                Size = new ImageSize(width, height),
                Fps = fps,
                Camera = Math.Max(1, request.Camera),
                PreferUdp = request.PreferUdp,
                Ports = UdpPorts,
                Bind = BindUdp,
                Route = DeviceConnections.RouteOf(device),
                Router = router,
                Time = Time,
            },
            ct,
            _loggerFactory.CreateLogger<RtspStreamProbe>()).ConfigureAwait(false);
    }

    /// <summary>
    /// The streamable sources: not disabled, the largest resolution by pixel count, the sensor's capture mode frame rate
    /// (else <see cref="LiveViewNegotiation.MaxFps"/>), H.265 before H.264, never MJPEG. No source without H.264 or H.265
    /// or without a resolution.
    /// </summary>
    public static IReadOnlyList<StreamProbeSource> ToSources(ImageCapabilities image, IReadOnlyDictionary<int, int>? maxFps)
    {
        ArgumentNullException.ThrowIfNull(image);
        var capabilities = LiveViewCapabilities.FromImageCapabilities(image);
        var codecs = LiveViewNegotiation.Preference.Where(capabilities.Codecs.Contains).Select(c => c == VideoCodecKind.H265 ? "h265" : "h264").ToList();
        if (codecs.Count == 0)
        {
            return [];
        }

        var result = new List<StreamProbeSource>();
        foreach (var source in capabilities.EffectiveSources)
        {
            var sizes = source.Resolutions.Count > 0 ? source.Resolutions : capabilities.Resolutions;
            if (sizes.Count == 0)
            {
                continue;
            }

            var largest = sizes.MaxBy(s => (long)s.Width * s.Height);
            var fps = maxFps is not null && maxFps.TryGetValue(source.Sensor, out var rate) && rate > 0 ? rate : LiveViewNegotiation.MaxFps;
            result.Add(new StreamProbeSource(source.Camera, source.Name, largest.Width, largest.Height, fps, codecs));
        }

        return result;
    }

    private async Task<Device> RequireAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        if (device.Status == DeviceStatus.CertificateChanged)
        {
            // Like the live view: no stream until the user trusts the device again.
            throw new DeviceStreamException(DeviceStreamError.Unauthorized, DeviceMessages.CertificateChanged);
        }

        return device;
    }

    private async Task<IReadOnlyList<StreamProbeSource>> ReadSourcesAsync(Guid deviceId, CancellationToken ct)
    {
        try
        {
            var client = await clients.CreateAsync(deviceId, ct).ConfigureAwait(false);
            var parameters = await client.ListParametersAsync(["Properties.Image", "Image"], ct).ConfigureAwait(false);
            var image = VapixParsers.ParseImageCapabilities(parameters);
            IReadOnlyDictionary<int, int>? maxFps = null;
            if (client is VapixClient vapix)
            {
                try
                {
                    maxFps = await vapix.GetCaptureModeMaxFpsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is VapixException or HttpRequestException or TimeoutException)
                {
                    // No capture mode API (older AXIS OS, encoders): the live view's highest frame rate.
                }
            }

            return ToSources(image, maxFps);
        }
        catch (CertificateChangedException ex)
        {
            throw new DeviceStreamException(DeviceStreamError.Unauthorized, DeviceMessages.CertificateChanged, ex);
        }
        catch (VapixAuthenticationException ex)
        {
            throw new DeviceStreamException(DeviceStreamError.Unauthorized, ex.BasicOnlyOverHttp ? DeviceMessages.BasicOnlyOverHttp : DeviceMessages.Unauthorized, ex);
        }
        catch (VapixException ex)
        {
            throw new DeviceStreamException(DeviceStreamError.Protocol, ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new DeviceStreamException(DeviceStreamError.Unreachable, DeviceMessages.Unreachable(ex.Message), ex);
        }
    }
}
