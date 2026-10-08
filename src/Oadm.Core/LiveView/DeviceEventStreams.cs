using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView;

/// <summary>
/// Server implementation of <see cref="IDeviceEventStreams"/> for core plugins: the RTSP event stream of a managed
/// device (<see cref="RtspMetadataSource"/>) with its stored credentials, which never leave the server.
/// </summary>
public sealed class DeviceEventStreams(
    DeviceRepository devices,
    CredentialStore credentials,
    ILoggerFactory? loggerFactory = null) : IDeviceEventStreams
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>RTSP port of the devices (tests use a loopback server).</summary>
    public int RtspPort { get; init; } = RtspClient.DefaultPort;

    public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        var stored = await credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        return await RtspMetadataSource.OpenAsync(
            new RtspMetadataOptions
            {
                Address = device.Address,
                Port = RtspPort,
                Credentials = stored is null ? null : new NetworkCredential(stored.UserName, stored.Password),
            },
            ct,
            _loggerFactory.CreateLogger<RtspMetadataSource>()).ConfigureAwait(false);
    }
}
