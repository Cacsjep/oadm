using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Devices;

/// <summary>
/// Keeps a device managed when its address changes: a task that re-addressed it moves the record
/// (<see cref="UpdateAddressAsync"/>), and the periodic mDNS browse re-finds a device that is unreachable at its
/// stored address but announces another one (<see cref="TryRelocateAsync"/>). Before the record moves, the device
/// answering at the new address must report the same serial number (authenticated basicdeviceinfo with the stored
/// credentials and the pinned certificate). Credentials and pin are kept, the change is published through the
/// device change feed and a full refresh is queued. Devices that OADM reaches by host name keep the host name.
/// </summary>
public sealed partial class DeviceAddressService : ITaskDeviceAddresses
{
    private readonly DeviceRepository _devices;
    private readonly VapixClientFactory _clients;
    private readonly Action<Guid> _queueRefresh;
    private readonly ILogger _logger;

    public DeviceAddressService(DeviceRepository devices, VapixClientFactory clients, DevicePollingService polling, ILogger<DeviceAddressService>? logger = null)
        : this(devices, clients, id => (polling ?? throw new ArgumentNullException(nameof(polling))).QueueRefresh([id]), logger)
    {
    }

    public DeviceAddressService(DeviceRepository devices, VapixClientFactory clients, Action<Guid> queueRefresh, ILogger<DeviceAddressService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(queueRefresh);
        _devices = devices;
        _clients = clients;
        _queueRefresh = queueRefresh;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Timeout of the identity check at the new address. Default 10 s.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<IVapixClient> CreateClientAsync(Guid deviceId, string address, CancellationToken ct)
    {
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        return await _clients.CreateForAddressAsync(device, NormalizeAddress(address), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="DeviceIdentityException">The device at the new address does not answer or has another serial.</exception>
    public async Task<DeviceAddressChangeResult> UpdateAddressAsync(Guid deviceId, string newAddress, CancellationToken ct)
    {
        var address = NormalizeAddress(newAddress);
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");
        return await MoveAsync(device, address, "changed by a task", ct).ConfigureAwait(false);
    }

    /// <summary>Clock for the certificate expiry rating after <see cref="UpdateTlsAsync"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <inheritdoc />
    /// <exception cref="DeviceIdentityException">
    /// The device does not answer with the new scheme, reports another serial number or presents another certificate.
    /// </exception>
    public async Task UpdateTlsAsync(Guid deviceId, string scheme, string? expectedFingerprintSha256, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        var https = string.Equals(scheme.Trim(), Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!https && !string.Equals(scheme.Trim(), Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"\"{scheme}\" is not http or https.", nameof(scheme));
        }

        var expected = https && !string.IsNullOrWhiteSpace(expectedFingerprintSha256) ? CertificatePinning.Normalize(expectedFingerprintSha256) : null;
        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {deviceId} not found.");

        using var client = await _clients.CreateUncachedAsync(device, device.Address, https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, expected, ct).ConfigureAwait(false);
        await VerifySerialAsync(client, device, https ? "over HTTPS" : "over HTTP", ct).ConfigureAwait(false);

        string? fingerprint = null;
        CertificateInfo? certificate = null;
        if (https)
        {
            fingerprint = client.CertificateFingerprint;
            if (fingerprint is null || (expected is not null && !string.Equals(fingerprint, expected, StringComparison.Ordinal)))
            {
                throw new DeviceIdentityException(
                    $"The device at {device.Address} does not present the expected certificate. The OADM device record is unchanged.");
            }

            certificate = client.ObservedCertificate;
        }

        var now = Time.GetUtcNow();
        var updated = await _devices.UpdateAsync(
            device.Id,
            d =>
            {
                d.Scheme = https ? DeviceScheme.Https : DeviceScheme.Http;
                if (https)
                {
                    d.CertFingerprintSha256 = fingerprint;
                    d.HttpsEnabled = true;
                    if (certificate is not null)
                    {
                        d.CertNotAfterUtc = certificate.NotAfterUtc;
                        d.CertTrust = certificate.TrustAt(now);
                        d.CertSubject = certificate.Subject;
                        d.CertIssuer = certificate.Issuer;
                        d.CertNameMatches = certificate.NameMatches;
                    }
                }
                else
                {
                    d.CertNotAfterUtc = null;
                    d.CertTrust = CertificateTrust.Unknown;
                    d.CertSubject = null;
                    d.CertIssuer = null;
                    d.CertNameMatches = null;
                }

                if (d.Status is DeviceStatus.CertificateChanged or DeviceStatus.Unreachable)
                {
                    d.Status = DeviceStatus.Unknown; // it answered; the queued refresh sets the real status
                }
            },
            ct).ConfigureAwait(false);
        if (updated is null)
        {
            throw new KeyNotFoundException($"Device {device.Id} not found.");
        }

        LogTlsChanged(device.Serial, device.Address, https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, fingerprint ?? "-");
        _queueRefresh(device.Id);
    }

    /// <summary>
    /// mDNS saw <paramref name="serial"/> at <paramref name="announcedAddress"/>: when that is a managed device which
    /// is unreachable at its stored address, verify it there and move the record. Never throws for device errors.
    /// </summary>
    public Task<DeviceAddressChangeResult> TryRelocateAsync(string serial, string announcedAddress, CancellationToken ct) =>
        TryRelocateAsync(serial, announcedAddress, "found again by mDNS", requireUnreachable: true, ct);

    /// <summary>
    /// <paramref name="serial"/> was seen at <paramref name="address"/> by <paramref name="reason"/> (mDNS, a DHCP lease):
    /// when that is a managed device addressed by IP with another address, verify it there and move the record. With
    /// <paramref name="requireUnreachable"/> only devices that are unreachable at their stored address move (mDNS); a DHCP
    /// lease is proof enough that the address changed, so it moves a device right away. Never throws for device errors.
    /// </summary>
    public async Task<DeviceAddressChangeResult> TryRelocateAsync(string serial, string announcedAddress, string reason, bool requireUnreachable, CancellationToken ct)
    {
        var device = await _devices.FindBySerialAsync(serial, ct).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceAddressChangeResult.NotManaged;
        }

        string address;
        try
        {
            address = NormalizeAddress(announcedAddress);
        }
        catch (ArgumentException)
        {
            return DeviceAddressChangeResult.NotVerified;
        }

        if (UsesHostName(device))
        {
            return DeviceAddressChangeResult.KeptHostName;
        }

        if (SameAddress(device.Address, address))
        {
            return DeviceAddressChangeResult.Unchanged;
        }

        if (requireUnreachable && device.Status != DeviceStatus.Unreachable)
        {
            return DeviceAddressChangeResult.StillReachable;
        }

        try
        {
            return await MoveAsync(device, address, reason, ct).ConfigureAwait(false);
        }
        catch (DeviceIdentityException ex)
        {
            LogRelocationRejected(device.Serial, device.Address, address, ex.Message);
            return DeviceAddressChangeResult.NotVerified;
        }
    }

    /// <summary>True when OADM addresses the device by host name rather than by an IP literal.</summary>
    public static bool UsesHostName(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return !IPAddress.TryParse(device.Address.Trim('[', ']'), out _);
    }

    private async Task<DeviceAddressChangeResult> MoveAsync(Device device, string address, string reason, CancellationToken ct)
    {
        if (UsesHostName(device))
        {
            LogKeptHostName(device.Serial, device.Address, address);
            return DeviceAddressChangeResult.KeptHostName;
        }

        if (SameAddress(device.Address, address))
        {
            return DeviceAddressChangeResult.Unchanged;
        }

        await VerifyIdentityAsync(device, address, ct).ConfigureAwait(false);

        var others = (await _devices.ListDevicesAsync(ct).ConfigureAwait(false))
            .Where(d => d.Id != device.Id && SameAddress(d.Address, address))
            .ToList();
        foreach (var other in others)
        {
            LogAddressShared(device.Serial, address, other.Serial);
        }

        var old = device.Address;
        var updated = await _devices.UpdateAsync(
            device.Id,
            d =>
            {
                d.Address = address;
                if (d.Status == DeviceStatus.Unreachable)
                {
                    d.Status = DeviceStatus.Unknown; // it answered at the new address; the queued refresh sets the real status
                }
            },
            ct).ConfigureAwait(false);
        if (updated is null)
        {
            throw new KeyNotFoundException($"Device {device.Id} not found.");
        }

        LogMoved(device.Serial, old, address, reason);
        _queueRefresh(device.Id);
        return DeviceAddressChangeResult.Updated;
    }

    private async Task VerifyIdentityAsync(Device device, string address, CancellationToken ct)
    {
        using var client = await _clients.CreateForAddressAsync(device, address, ct).ConfigureAwait(false);
        await VerifySerialAsync(client, device, "at " + address, ct).ConfigureAwait(false);
    }

    /// <summary>Authenticated basicdeviceinfo through <paramref name="client"/>: the serial number must be the device's.</summary>
    private async Task VerifySerialAsync(VapixClient client, Device device, string where, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(VerifyTimeout);
        BasicDeviceInfo info;
        try
        {
            info = await client.GetBasicDeviceInfoAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceIdentityException($"The device does not answer {where}.");
        }
        catch (CertificateChangedException ex)
        {
            throw new DeviceIdentityException($"The device {where} does not present the expected certificate. The OADM device record is unchanged.", ex);
        }
        catch (Exception ex) when (ex is VapixException or HttpRequestException or IOException)
        {
            throw new DeviceIdentityException($"The device {where} could not be identified: {ex.Message}", ex);
        }

        if (!DeviceSerial.TryNormalize(info.SerialNumber, out var serial) || !string.Equals(serial, device.Serial, StringComparison.Ordinal))
        {
            throw new DeviceIdentityException(
                $"The device {where} has serial number {info.SerialNumber}, not {device.Serial}. The OADM device record is unchanged.");
        }
    }

    private static string NormalizeAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var trimmed = address.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(trimmed, out var ip) || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException($"\"{address}\" is not an IP address.", nameof(address));
        }

        return ip.ToString();
    }

    private static bool SameAddress(string a, string b) =>
        IPAddress.TryParse(a.Trim('[', ']'), out var x) && IPAddress.TryParse(b.Trim('[', ']'), out var y)
            ? x.Equals(y)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} moved from {OldAddress} to {NewAddress} ({Reason})")]
    private partial void LogMoved(string serial, string oldAddress, string newAddress, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} at {Address}: OADM now connects over {Scheme} (certificate {Fingerprint})")]
    private partial void LogTlsChanged(string serial, string address, string scheme, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} is reached by host name {HostName}; it is kept (new address {NewAddress})")]
    private partial void LogKeptHostName(string serial, string hostName, string newAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {Serial} announced at {NewAddress} (stored {OldAddress}) was not moved: {Reason}")]
    private partial void LogRelocationRejected(string serial, string oldAddress, string newAddress, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {Serial} moves to {Address}, which the record of device {OtherSerial} still has")]
    private partial void LogAddressShared(string serial, string address, string otherSerial);
}
