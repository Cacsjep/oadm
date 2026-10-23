using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
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
/// at the same time, each bounded by the timeout. Device groups (SDK 0.4.0): the methods with an <see cref="ICallContext"/>
/// reach only the devices of the user's view; for a user with a device group a device id outside the view answers like an
/// unknown one (<see cref="DevicesNotAvailableException"/>, nothing is sent). The methods without one use the repository and
/// factory the service was made with (the plugin's context: in-process and background callers).
/// </summary>
public sealed partial class SnapshotService : IDisposable
{
    private static readonly TimeSpan SourceCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Up to this many ids are looked up one by one; more use one list call.</summary>
    private const int FindOneByOneLimit = 16;

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
        Parallelism = Math.Max(1, parallelism);
        _gate = new SemaphoreSlim(Parallelism);
        Timeout = timeout ?? SnapshotReportPluginInfo.SnapshotTimeout;
    }

    /// <summary>Time one device request may take.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The devices and clients the service was made with (the plugin's context).</summary>
    private DeviceAccess Default => new(_devices, _vapix, Strict: false);

    /// <summary>The repository the service was made with.</summary>
    internal IDeviceRepository DefaultDevices => _devices;

    /// <summary>The text of a managed device without video (a speaker, an I/O module).</summary>
    public const string NoVideo = "The device has no video";

    /// <summary>The factory the service was made with.</summary>
    internal IVapixClientFactory DefaultVapix => _vapix;

    /// <summary>The managed video devices (all for an empty list), as facts in address order.</summary>
    public Task<IReadOnlyList<IDeviceInfo>> GetVideoDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct) =>
        GetVideoDevicesAsync(Default, deviceIds, ct);

    /// <summary>For a user with a device group: throws <see cref="DevicesNotAvailableException"/> when any id is outside the view.</summary>
    public static async Task RequireAsync(ICallContext caller, IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        _ = await FindAllAsync(DeviceAccess.Of(caller), deviceIds, ct).ConfigureAwait(false);
    }

    /// <summary>The managed devices of <paramref name="deviceIds"/> (all for an empty list), with or without video.</summary>
    internal static async Task<IReadOnlyList<IDeviceInfo>> GetDevicesAsync(DeviceAccess access, IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        return deviceIds.Count == 0 ? await access.Devices.ListAsync(ct).ConfigureAwait(false) : await FindAllAsync(access, deviceIds, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<IDeviceInfo>> GetVideoDevicesAsync(DeviceAccess access, IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var devices = await GetDevicesAsync(access, deviceIds, ct).ConfigureAwait(false);
        return [.. devices.Where(d => d.HasVideo).OrderBy(d => d.Address, AddressComparer.Instance).ThenBy(d => d.Serial, StringComparer.Ordinal)];
    }

    /// <summary>The devices of <paramref name="deviceIds"/>; a strict access (a device group) refuses the whole list when one is missing.</summary>
    private static async Task<IReadOnlyList<IDeviceInfo>> FindAllAsync(DeviceAccess access, IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        List<IDeviceInfo> found;
        var wanted = deviceIds as IReadOnlySet<Guid> ?? deviceIds.ToHashSet();
        if (wanted.Count > FindOneByOneLimit)
        {
            // Thousands of ids (a report of a large site): one list call and a set lookup, not one lookup per id.
            found = [.. (await access.Devices.ListAsync(ct).ConfigureAwait(false)).Where(d => wanted.Contains(d.Id))];
        }
        else
        {
            found = [];
            foreach (var id in wanted)
            {
                if (await access.Devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
                {
                    found.Add(device);
                }
            }
        }

        if (access.Strict && found.Count < wanted.Count)
        {
            throw new DevicesNotAvailableException(wanted.Count - found.Count);
        }

        return found;
    }

    /// <summary>
    /// One tile per video source of the requested video devices (non-video devices are skipped). The page's list
    /// (<paramref name="refreshSources"/> true) reads the sources again; a report uses the sources read in the last
    /// minutes. Bounded fan-out: <see cref="SnapshotReportPluginInfo.Parallelism"/> workers, never one task per device.
    /// </summary>
    public Task<ListSourcesResult> ListSourcesAsync(ListSourcesRequest request, CancellationToken ct, bool refreshSources = true) =>
        ListSourcesAsync(Default, request, refreshSources, ct);

    /// <summary><see cref="ListSourcesAsync(ListSourcesRequest, CancellationToken, bool)"/> for the devices of the user's view.</summary>
    public Task<ListSourcesResult> ListSourcesAsync(ICallContext caller, ListSourcesRequest request, CancellationToken ct, bool refreshSources = true) =>
        ListSourcesAsync(DeviceAccess.Of(caller), request, refreshSources, ct);

    internal async Task<ListSourcesResult> ListSourcesAsync(DeviceAccess access, ListSourcesRequest request, bool refreshSources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var devices = await GetVideoDevicesAsync(access, request.DeviceIds, ct).ConfigureAwait(false);
        var perDevice = new IReadOnlyList<SnapshotTile>[devices.Count];
        await ForEachBoundedAsync(devices.Count, async i => perDevice[i] = await ListDeviceAsync(access.Vapix, devices[i], refreshSources, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return new ListSourcesResult { Tiles = [.. perDevice.SelectMany(t => t)] };
    }

    /// <summary>
    /// Runs <paramref name="body"/> for 0..count-1 with <see cref="Parallelism"/> workers (thousands of items: a few
    /// tasks, not one per item; the device requests inside are bounded by the shared gate as well).
    /// </summary>
    public async Task ForEachBoundedAsync(int count, Func<int, Task> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var next = -1;
        async Task WorkerAsync()
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < count)
            {
                ct.ThrowIfCancellationRequested();
                await body(i).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(Parallelism, Math.Max(count, 1))).Select(_ => WorkerAsync())).ConfigureAwait(false);
    }

    /// <summary>Device requests running at the same time (all callers together).</summary>
    public int Parallelism { get; }

    /// <summary>Takes one snapshot. Never throws for device problems; they end up in <see cref="CapturedSnapshot.Error"/>.</summary>
    public async Task<CapturedSnapshot> TakeAsync(Guid deviceId, int camera, int maxWidth, int maxHeight, CancellationToken ct) =>
        await TakeAsync(_vapix, await _devices.FindAsync(deviceId, ct).ConfigureAwait(false), camera, maxWidth, maxHeight, ct).ConfigureAwait(false);

    /// <summary>
    /// Takes one snapshot of a device of the user's view. For a user with a device group a device outside the view answers
    /// like an unknown one (<see cref="DevicesNotAvailableException"/>); device problems end up in <see cref="CapturedSnapshot.Error"/>.
    /// </summary>
    public async Task<CapturedSnapshot> TakeAsync(ICallContext caller, Guid deviceId, int camera, int maxWidth, int maxHeight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var device = await caller.Devices.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null && !caller.HasAllDevices)
        {
            throw new DevicesNotAvailableException();
        }

        return await TakeAsync(caller.Vapix, device, camera, maxWidth, maxHeight, ct).ConfigureAwait(false);
    }

    /// <summary>Takes one snapshot of a device already looked up (null = no longer managed). Never throws for device problems.</summary>
    public Task<CapturedSnapshot> TakeAsync(IDeviceInfo? device, int camera, int maxWidth, int maxHeight, CancellationToken ct) =>
        TakeAsync(_vapix, device, camera, maxWidth, maxHeight, ct);

    /// <summary>Takes one snapshot of a device already looked up, with the clients of <paramref name="vapix"/>.</summary>
    public async Task<CapturedSnapshot> TakeAsync(IVapixClientFactory vapix, IDeviceInfo? device, int camera, int maxWidth, int maxHeight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        if (device is null)
        {
            return Failed(DeviceMessages.NoLongerAvailable(1));
        }

        var deviceId = device.Id;

        if (!device.HasVideo)
        {
            return Failed(NoVideo);
        }

        if (SnapshotRequests.StatusError(device) is { } statusError)
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
                var client = await vapix.CreateAsync(deviceId, timeout.Token).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<SnapshotTile>> ListDeviceAsync(IVapixClientFactory vapix, IDeviceInfo device, bool refreshSources, CancellationToken ct)
    {
        if (SnapshotRequests.StatusError(device) is { } statusError)
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
                var client = await vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
                if (refreshSources)
                {
                    _sources.TryRemove(device.Id, out _);
                }

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

    /// <summary>Where devices come from: a user's call (strict for a device group) or the plugin's context.</summary>
    internal sealed record DeviceAccess(IDeviceRepository Devices, IVapixClientFactory Vapix, bool Strict)
    {
        public static DeviceAccess Of(ICallContext caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            return new(caller.Devices, caller.Vapix, !caller.HasAllDevices);
        }
    }

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
