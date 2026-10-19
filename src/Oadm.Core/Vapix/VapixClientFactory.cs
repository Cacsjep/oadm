using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>Opens a <see cref="VapixClient"/> for connection options. Tests replace it to simulate devices.</summary>
public interface IVapixConnector
{
    VapixClient Connect(VapixConnectionOptions options);
}

/// <summary>
/// Real network connector: <see cref="VapixClient.Create"/>, with the server's extra trust anchors, connecting on each
/// device's route through <paramref name="router"/> (default: the server's own network only).
/// </summary>
public sealed class VapixConnector(TrustAnchorRegistry? trustAnchors = null, DeviceRouter? router = null) : IVapixConnector
{
    public VapixClient Connect(VapixConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return VapixClient.Create(options.TrustAnchors is null && trustAnchors is not null ? options with { TrustAnchors = trustAnchors } : options, router);
    }
}

/// <summary>
/// Builds authenticated <see cref="VapixClient"/>s for managed devices from the device row
/// (address, scheme, pinned certificate fingerprint) and the <see cref="CredentialStore"/>.
/// Clients are cached per device and reused while route, address, scheme, pin and credentials are
/// unchanged; a change creates a new client and disposes the old one. Removing a device, or a change of its route or
/// address, disposes its client. Devices without stored credentials get an anonymous client.
/// </summary>
public sealed partial class VapixClientFactory : IVapixClientFactory, IDisposable
{
    private readonly DeviceRepository _devices;
    private readonly CredentialStore _credentials;
    private readonly IVapixConnector _connector;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly Lock _sync = new();
    private bool _disposed;

    public VapixClientFactory(
        DeviceRepository devices,
        CredentialStore credentials,
        IVapixConnector? connector = null,
        ILogger<VapixClientFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(credentials);
        _devices = devices;
        _credentials = credentials;
        _connector = connector ?? new VapixConnector();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _devices.Changes.Changed += OnDeviceChanged;
    }

    /// <summary>Request timeout of created clients.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Number of cached clients (diagnostics and tests).</summary>
    public int CachedCount => _cache.Count;

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    public async Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) =>
        await GetClientAsync(deviceId, ct).ConfigureAwait(false);

    /// <summary>The cached or a new client for the device. Do not dispose it; the factory owns it.</summary>
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    public async Task<VapixClient> GetClientAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        return await GetClientAsync(device, ct).ConfigureAwait(false);
    }

    /// <summary>The cached or a new client for an already loaded device row.</summary>
    public async Task<VapixClient> GetClientAsync(Device device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var credentials = await _credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        var options = DeviceConnections.For(
            device,
            credentials is null ? null : new NetworkCredential(credentials.UserName, credentials.Password),
            RequestTimeout);
        var key = CacheKey(options.Route, options.Address, options.Scheme, options.PinnedCertificateFingerprint, credentials);

        VapixClient? stale = null;
        VapixClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(device.Id, out var entry) && entry.Key == key)
            {
                return entry.Client;
            }

            client = _connector.Connect(options);
            stale = entry?.Client;
            _cache[device.Id] = new Entry(key, options.Route, options.Address, client);
        }

        stale?.Dispose();
        LogClientCreated(device.Id, client.BaseAddress, credentials is not null);
        return client;
    }

    /// <summary>
    /// A new, uncached client for the device at another address (same credentials, scheme and pinned
    /// certificate), e.g. to verify a re-addressed device before its record moves. The caller disposes it.
    /// </summary>
    public Task<VapixClient> CreateForAddressAsync(Device device, string address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        return CreateUncachedAsync(device, address, DeviceConnections.SchemeOf(device), device.CertFingerprintSha256, ct);
    }

    /// <summary>
    /// A new, uncached client for the device with explicit address, scheme and certificate pin (null = trust on first
    /// use), e.g. to verify the device after a task changed its web server certificate. The caller disposes it.
    /// </summary>
    public async Task<VapixClient> CreateUncachedAsync(Device device, string address, string scheme, string? pinnedFingerprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var credentials = await _credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        var client = _connector.Connect(DeviceConnections.For(
            device,
            credentials is null ? null : new NetworkCredential(credentials.UserName, credentials.Password),
            RequestTimeout) with
        {
            Address = address.Trim(),
            Scheme = scheme,
            PinnedCertificateFingerprint = pinnedFingerprint,
        });
        LogClientCreated(device.Id, client.BaseAddress, credentials is not null);
        return client;
    }

    /// <summary>
    /// A new, uncached client for the device (stored address, scheme and pinned certificate) with credentials that are
    /// not stored yet, e.g. to check a login the technician typed before it replaces the stored one. The caller disposes it.
    /// </summary>
    public VapixClient CreateWithCredentials(Device device, string userName, string password)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = _connector.Connect(DeviceConnections.For(device, new NetworkCredential(userName, password), RequestTimeout));
        LogClientCreated(device.Id, client.BaseAddress, true);
        return client;
    }

    /// <summary>Drops and disposes the cached client of a device (e.g. after its credentials changed).</summary>
    public void Invalidate(Guid deviceId)
    {
        if (_cache.TryRemove(deviceId, out var entry))
        {
            entry.Client.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _devices.Changes.Changed -= OnDeviceChanged;
        foreach (var id in _cache.Keys.ToArray())
        {
            Invalidate(id);
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChange change)
    {
        if (change.Kind == DeviceChangeKind.Removed)
        {
            Invalidate(change.DeviceId);
            return;
        }

        // A device that moved (another route or address) drops its client at once: its kept connections lead to the
        // old place.
        if (change.Device is { } device && _cache.TryGetValue(change.DeviceId, out var entry)
            && (entry.Route != DeviceConnections.RouteOf(device) || !string.Equals(entry.Address, device.Address, StringComparison.Ordinal))
            && _cache.TryRemove(KeyValuePair.Create(change.DeviceId, entry)))
        {
            entry.Client.Dispose();
        }
    }

    /// <summary>Cache key; the password is only kept as a hash so it does not linger in another string.</summary>
    private static string CacheKey(DeviceRoute route, string address, string scheme, string? pin, DeviceCredentials? credentials)
    {
        var secret = credentials is null
            ? "-"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.UserName + "\n" + credentials.Password)));
        return string.Join('|', route.ToString(), scheme, address, pin ?? "-", secret);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "VAPIX client for device {DeviceId} at {BaseAddress} (credentials: {HasCredentials})")]
    private partial void LogClientCreated(Guid deviceId, Uri baseAddress, bool hasCredentials);

    private sealed record Entry(string Key, DeviceRoute Route, string Address, VapixClient Client);
}
