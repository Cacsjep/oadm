using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView;

/// <summary>
/// Server implementation of <see cref="IDeviceEventStreams"/> for core plugins: the RTSP event stream of a managed
/// device (<see cref="RtspMetadataSource"/>) with its stored credentials, which never leave the server. A device's RTSP
/// port (a port forward, e.g. a camera behind NAT) is stored in the server setting <see cref="RtspPortsKey"/>
/// (JSON object device id -> port); devices without an entry use <see cref="RtspPort"/>.
/// </summary>
public sealed class DeviceEventStreams(
    DeviceRepository devices,
    CredentialStore credentials,
    ILoggerFactory? loggerFactory = null,
    ServerSettingsStore? settings = null) : IDeviceEventStreams, IDisposable
{
    public const string RtspPortsKey = "Devices.RtspPorts";

    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>RTSP port of the devices without a stored port (tests use a loopback server).</summary>
    public int RtspPort { get; init; } = RtspClient.DefaultPort;

    public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        var stored = await credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        var port = await GetRtspPortAsync(deviceId, ct).ConfigureAwait(false) ?? RtspPort;
        return await RtspMetadataSource.OpenAsync(
            new RtspMetadataOptions
            {
                Address = device.Address,
                Port = port,
                Credentials = stored is null ? null : new NetworkCredential(stored.UserName, stored.Password),
            },
            ct,
            _loggerFactory.CreateLogger<RtspMetadataSource>()).ConfigureAwait(false);
    }

    public async Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct)
    {
        var ports = await ReadPortsAsync(ct).ConfigureAwait(false);
        return ports.TryGetValue(Key(deviceId), out var port) ? port : null;
    }

    public async Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The RTSP port must be from 1 to 65535.");
        }

        if (settings is null)
        {
            throw new NotSupportedException("This server cannot store RTSP ports.");
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ports = await ReadPortsAsync(ct).ConfigureAwait(false);
            var key = Key(deviceId);
            bool changed;
            if (port is null or RtspClient.DefaultPort)
            {
                changed = ports.Remove(key);
            }
            else
            {
                changed = !ports.TryGetValue(key, out var old) || old != port.Value;
                ports[key] = port.Value;
            }

            if (changed)
            {
                await settings.SetJsonAsync(RtspPortsKey, ports.Count == 0 ? null : JsonSerializer.Serialize(ports), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    private static string Key(Guid deviceId) => deviceId.ToString("D", CultureInfo.InvariantCulture);

    private async Task<Dictionary<string, int>> ReadPortsAsync(CancellationToken ct)
    {
        if (settings is null)
        {
            return [];
        }

        var json = await settings.GetJsonAsync(RtspPortsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
