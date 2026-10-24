using System.Collections.Frozen;
using System.Net;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The managed devices in memory for the certificate check, which runs inside the TLS handshake and may only do lookups
/// (docs/spec/radius-server.md "Code (protocol)"): by serial number (the certificate's <c>axis-&lt;serial&gt;.local</c> or
/// device ID serial, and the MAC address guess) and by id. Read from the full device list (background work, never a
/// user's call) every <see cref="RefreshInterval"/>, and soon after a lookup missed (a device added a moment ago), at most
/// once per <see cref="MissRefreshInterval"/>. A removed device is refused at the latest after one interval.
/// </summary>
public sealed class DeviceIndex(IDeviceRepository devices, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MissRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private long _lastReadTicks = long.MinValue;
    private int _missPending;

    /// <summary>Raised (on the thread pool) after a lookup missed; the service reads the list again.</summary>
    public event EventHandler? Missed;

    /// <summary>How many devices the index holds.</summary>
    public int Count => _snapshot.ById.Count;

    /// <summary>The managed device with this serial (12 hex digits, any case), or null.</summary>
    public IDeviceInfo? BySerial(string? serial)
    {
        if (string.IsNullOrEmpty(serial))
        {
            return null;
        }

        if (_snapshot.BySerial.TryGetValue(serial, out var device))
        {
            return device;
        }

        if (Interlocked.Exchange(ref _missPending, 1) == 0)
        {
            Missed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    /// <summary>The managed device with this id, or null (no read on a miss).</summary>
    public IDeviceInfo? ById(Guid id) => _snapshot.ById.GetValueOrDefault(id);

    /// <summary>The device a MAC address names (an Axis serial is its MAC address): a guess, never trusted; no read on a miss.</summary>
    public IDeviceInfo? GuessByMac(string? macSerial) =>
        string.IsNullOrEmpty(macSerial) ? null : _snapshot.BySerial.GetValueOrDefault(macSerial);

    /// <summary>Reads the device list now (or not when <paramref name="onlyAfterMiss"/> and the last read is recent).</summary>
    public async Task RefreshAsync(bool onlyAfterMiss, CancellationToken ct)
    {
        if (onlyAfterMiss && time.GetElapsedTime(Volatile.Read(ref _lastReadTicks)) < MissRefreshInterval && _lastReadTicks != long.MinValue)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _missPending, 0);
            var list = await devices.ListAsync(ct).ConfigureAwait(false);
            var bySerial = new Dictionary<string, IDeviceInfo>(list.Count, StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<Guid, IDeviceInfo>(list.Count);
            foreach (var device in list)
            {
                if (!string.IsNullOrEmpty(device.Serial))
                {
                    bySerial.TryAdd(device.Serial, device);
                }

                byId[device.Id] = device;
            }

            _snapshot = new Snapshot(bySerial.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), byId.ToFrozenDictionary());
            Volatile.Write(ref _lastReadTicks, time.GetTimestamp());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>The host part of a device's address when it is an IP address, else the address as stored.</summary>
    public static string IpAddressOf(IDeviceInfo? device)
    {
        if (device is null)
        {
            return string.Empty;
        }

        var text = device.Address.Trim();
        if (IPAddress.TryParse(text.Trim('[', ']'), out var ip))
        {
            return ip.ToString();
        }

        var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : text;
    }

    /// <summary>The device as the page names it: its host name, else its serial number.</summary>
    public static string NameOf(IDeviceInfo? device) =>
        device is null ? string.Empty : !string.IsNullOrWhiteSpace(device.HostName) ? device.HostName.Trim() : device.Serial;

    private sealed record Snapshot(FrozenDictionary<string, IDeviceInfo> BySerial, FrozenDictionary<Guid, IDeviceInfo> ById)
    {
        public static Snapshot Empty { get; } = new(FrozenDictionary<string, IDeviceInfo>.Empty, FrozenDictionary<Guid, IDeviceInfo>.Empty);
    }
}
