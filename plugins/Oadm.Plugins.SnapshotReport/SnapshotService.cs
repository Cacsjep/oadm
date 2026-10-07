using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport;

/// <summary>A snapshot with its JPEG bytes (server side; the page gets it as base64).</summary>
public sealed record CapturedSnapshot(byte[]? Jpeg, int Width, int Height, DateTimeOffset? CapturedUtc, string? Error)
{
    public bool IsOk => Jpeg is not null && Error is null;
}

/// <summary>
/// Lists the video sources of managed devices (one tile per source, live view discovery via
/// <see cref="IVapixClient.GetVideoSourcesAsync"/>) and takes JPEG snapshots with the stored credentials.
/// Read-only for the devices. At most <see cref="SnapshotReportPluginInfo.Parallelism"/> device requests run
/// at the same time, each bounded by the timeout.
/// </summary>
public sealed partial class SnapshotService : IDisposable
{
    private static readonly TimeSpan SourceCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<VideoSource> Sources)> _sources = new();

    public SnapshotService(IDeviceRepository devices, IVapixClientFactory vapix, TimeProvider? time = null, int parallelism = SnapshotReportPluginInfo.Parallelism, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(vapix);
        _devices = devices;
        _vapix = vapix;
        _time = time ?? TimeProvider.System;
        _gate = new SemaphoreSlim(Math.Max(1, parallelism));
        Timeout = timeout ?? SnapshotReportPluginInfo.SnapshotTimeout;
    }

    /// <summary>Time one device request may take.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The managed video devices (all for an empty list), as facts in address order.</summary>
    public async Task<IReadOnlyList<IDeviceInfo>> GetVideoDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        IEnumerable<IDeviceInfo> devices;
        if (deviceIds.Count == 0)
        {
            devices = await _devices.ListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var found = new List<IDeviceInfo>();
            foreach (var id in deviceIds.Distinct())
            {
                if (await _devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
                {
                    found.Add(device);
                }
            }

            devices = found;
        }

        return [.. devices.Where(d => d.HasVideo).OrderBy(d => d.Address, AddressComparer.Instance).ThenBy(d => d.Serial, StringComparer.Ordinal)];
    }

    /// <summary>One tile per video source of the requested video devices (non-video devices are skipped).</summary>
    public async Task<ListSourcesResult> ListSourcesAsync(ListSourcesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var devices = await GetVideoDevicesAsync(request.DeviceIds, ct).ConfigureAwait(false);
        var perDevice = await Task.WhenAll(devices.Select(d => ListDeviceAsync(d, ct))).ConfigureAwait(false);
        return new ListSourcesResult { Tiles = [.. perDevice.SelectMany(t => t)] };
    }

    /// <summary>Takes one snapshot. Never throws for device problems; they end up in <see cref="CapturedSnapshot.Error"/>.</summary>
    public async Task<CapturedSnapshot> TakeAsync(Guid deviceId, int camera, int maxWidth, int maxHeight, CancellationToken ct)
    {
        var device = await _devices.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            return Failed("The device is no longer managed");
        }

        if (!device.HasVideo)
        {
            return Failed("The device has no video");
        }

        if (SnapshotRequests.StatusError(device.Status) is { } statusError)
        {
            return Failed(statusError);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var client = await _vapix.CreateAsync(deviceId, timeout.Token).ConfigureAwait(false);
                var sources = await GetSourcesAsync(deviceId, client, timeout.Token).ConfigureAwait(false);
                var source = sources.FirstOrDefault(s => s.Camera == camera);
                if (sources.Count > 0 && source is null)
                {
                    return Failed(string.Create(CultureInfo.InvariantCulture, $"The device has no video source {camera}"));
                }

                var resolution = SnapshotRequests.ChooseResolution(source, maxWidth, maxHeight);
                using var request = SnapshotRequests.Build(camera, resolution, Timeout);
                using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK && SnapshotRequests.IsJpeg(body))
                {
                    SnapshotRequests.TryGetJpegSize(body, out var width, out var height);
                    return new CapturedSnapshot(body, width, height, _time.GetUtcNow(), null);
                }

                var text = body.Length is > 0 and < 64 * 1024 ? System.Text.Encoding.UTF8.GetString(body) : null;
                return Failed(SnapshotRequests.HttpError(response.StatusCode, response.Content.Headers.ContentType?.MediaType, text));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Failed(SnapshotRequests.ExceptionError(ex, Timeout));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tiles of one device: one tile for one source, else one per source labeled with the source name
    /// ("View Area 1") or "Sensor n" / "Channel n" for generic names. <paramref name="error"/> gives one
    /// error tile for the whole device.
    /// </summary>
    public static IReadOnlyList<SnapshotTile> Expand(IDeviceInfo device, IReadOnlyList<VideoSource> sources, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(sources);
        var facts = ToFacts(device);
        if (error is not null || sources.Count == 0)
        {
            return [new SnapshotTile { Device = facts, Camera = 1, SourceCount = 1, Title = facts.Address, Error = error }];
        }

        if (sources.Count == 1)
        {
            return [new SnapshotTile { Device = facts, Camera = sources[0].Camera, SourceCount = 1, Title = facts.Address }];
        }

        return [.. sources.Select(s =>
        {
            var label = SourceLabel(device.Category, s);
            return new SnapshotTile { Device = facts, Camera = s.Camera, SourceLabel = label, SourceCount = sources.Count, Title = $"{facts.Address} - {label}" };
        })];
    }

    /// <summary>"View Area 2" as the device names it; "Sensor 2" (encoders: "Channel 2") for empty or generic "Camera n" names.</summary>
    public static string SourceLabel(DeviceCategory category, VideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var name = source.Name?.Trim() ?? string.Empty;
        if (name.Length > 0 && !GenericNameRegex().IsMatch(name))
        {
            return name;
        }

        var kind = category == DeviceCategory.Encoder ? "Channel" : "Sensor";
        return string.Create(CultureInfo.InvariantCulture, $"{kind} {source.Camera}");
    }

    public static DeviceFacts ToFacts(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceFacts
        {
            DeviceId = device.Id,
            Address = device.Address,
            HostName = device.HostName,
            Model = device.Model,
            Serial = device.Serial,
            Firmware = device.FirmwareVersion,
            Status = device.Status.ToString(),
            CertNotAfterUtc = device.CertNotAfterUtc,
            CertTrust = device.CertTrustName,
        };
    }

    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyList<SnapshotTile>> ListDeviceAsync(IDeviceInfo device, CancellationToken ct)
    {
        if (SnapshotRequests.StatusError(device.Status) is { } statusError)
        {
            return Expand(device, [], statusError);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var client = await _vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
                _sources.TryRemove(device.Id, out _);
                var sources = await GetSourcesAsync(device.Id, client, timeout.Token).ConfigureAwait(false);
                return Expand(device, sources);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Expand(device, [], SnapshotRequests.ExceptionError(ex, Timeout));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<VideoSource>> GetSourcesAsync(Guid deviceId, IVapixClient client, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_sources.TryGetValue(deviceId, out var cached) && now - cached.At < SourceCacheLifetime)
        {
            return cached.Sources;
        }

        var sources = await client.GetVideoSourcesAsync(ct).ConfigureAwait(false);
        _sources[deviceId] = (now, sources);
        return sources;
    }

    private static CapturedSnapshot Failed(string error) => new(null, 0, 0, null, error);

    [GeneratedRegex(@"^Camera(\s*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericNameRegex();

    /// <summary>IPv4 addresses numerically, then everything else alphabetically.</summary>
    private sealed class AddressComparer : IComparer<string>
    {
        public static AddressComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var a = IPAddress.TryParse(x, out var ax) ? ax.GetAddressBytes() : null;
            var b = IPAddress.TryParse(y, out var bx) ? bx.GetAddressBytes() : null;
            if (a is not null && b is not null)
            {
                var byLength = a.Length.CompareTo(b.Length);
                return byLength != 0 ? byLength : ((ReadOnlySpan<byte>)a).SequenceCompareTo(b);
            }

            if (a is not null)
            {
                return -1;
            }

            return b is not null ? 1 : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
