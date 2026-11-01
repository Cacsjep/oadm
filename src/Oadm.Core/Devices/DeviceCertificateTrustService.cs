using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Devices;

/// <summary>The certificate a device with status Certificate changed presents now (the "Trust new certificate" dialog).</summary>
/// <param name="PinnedFingerprint">The fingerprint OADM trusts (SHA-256, upper hex).</param>
/// <param name="Certificate">The certificate the device presents now; null when it could not be read (<paramref name="Message"/> says why).</param>
/// <param name="Message">User facing reason when there is nothing to trust.</param>
public sealed record PresentedDeviceCertificate(Guid DeviceId, string? PinnedFingerprint, CertificateInfo? Certificate, string? Message = null);

/// <summary>Result of "Trust new certificate" for one device.</summary>
/// <param name="Message">User facing reason when not <paramref name="Ok"/>.</param>
public sealed record DeviceCertificateTrustResult(Guid DeviceId, bool Ok, string? Message = null);

/// <summary>
/// "Trust new certificate" (device context menu) for devices with status Certificate changed, instead of removing and
/// adding them again (tags, history and the rest of the record stay). <see cref="ReadAsync"/> shows what each device
/// presents now: the TLS handshake with the old pin fails before any request, so nothing (no login) reaches the device.
/// <see cref="TrustAsync"/> pins only the fingerprint the user confirmed: <see cref="DeviceAddressService.UpdateTlsAsync"/>
/// connects pinned to it, checks the serial number with the stored credentials, stores the certificate and queues a
/// refresh. Another certificate by then, another serial or a refused login leave the record unchanged.
/// </summary>
public sealed partial class DeviceCertificateTrustService(
    DeviceRepository devices,
    VapixClientFactory clients,
    DeviceAddressService addresses,
    ILogger<DeviceCertificateTrustService>? logger = null)
{
    public const string NotChangedMessage = "The device does not show Certificate changed. Nothing was changed.";

    public const string SameAgainMessage = "The device presents the trusted certificate again.";

    public const string OtherCertificateMessage = "The device now presents another certificate than shown. Nothing was changed.";

    public const string NoCertificateMessage = "The device presented no certificate.";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Devices handled at the same time. Default 8.</summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>The certificate every device presents now, in the order of the distinct ids.</summary>
    public async Task<IReadOnlyList<PresentedDeviceCertificate>> ReadAsync(ViewedDevices viewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        var results = new ConcurrentDictionary<Guid, PresentedDeviceCertificate>();
        var ids = viewed.Ids.Distinct().ToList();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
            async (id, token) =>
            {
                var device = await devices.GetAsync(id, token).ConfigureAwait(false);
                results[id] = device is null
                    ? new PresentedDeviceCertificate(id, null, null, DeviceMessages.Removed)
                    : await ReadOneAsync(device, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return [.. ids.Select(id => results[id])];
    }

    /// <summary>
    /// Pins <paramref name="fingerprints"/> (device id to the fingerprint the user confirmed). Results in the order of the
    /// distinct ids.
    /// </summary>
    public async Task<IReadOnlyList<DeviceCertificateTrustResult>> TrustAsync(ViewedDevices viewed, IReadOnlyDictionary<Guid, string> fingerprints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        ArgumentNullException.ThrowIfNull(fingerprints);
        var results = new ConcurrentDictionary<Guid, DeviceCertificateTrustResult>();
        var ids = viewed.Ids.Distinct().ToList();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
            async (id, token) =>
            {
                var device = await devices.GetAsync(id, token).ConfigureAwait(false);
                results[id] = device is null
                    ? new DeviceCertificateTrustResult(id, false, DeviceMessages.Removed)
                    : !fingerprints.TryGetValue(id, out var fingerprint) || string.IsNullOrWhiteSpace(fingerprint)
                        ? new DeviceCertificateTrustResult(id, false, OtherCertificateMessage)
                        : await TrustOneAsync(device, fingerprint, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return [.. ids.Select(id => results[id])];
    }

    private async Task<PresentedDeviceCertificate> ReadOneAsync(Device device, CancellationToken ct)
    {
        var pinned = device.CertFingerprintSha256;
        if (device.Status != DeviceStatus.CertificateChanged || device.Scheme != DeviceScheme.Https)
        {
            return new PresentedDeviceCertificate(device.Id, pinned, null, NotChangedMessage);
        }

        try
        {
            // Pinned to the old certificate: a new one fails the handshake, before the request (and any login) is sent.
            using var client = await clients.CreateUncachedAsync(device, device.Address, Uri.UriSchemeHttps, pinned, ct).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (CertificateChangedException)
            {
                // Expected: the handshake saw the new certificate.
            }

            var presented = client.ObservedCertificate;
            if (presented is null)
            {
                return new PresentedDeviceCertificate(device.Id, pinned, null, NoCertificateMessage);
            }

            return pinned is not null && string.Equals(presented.Fingerprint, CertificatePinning.Normalize(pinned), StringComparison.Ordinal)
                ? new PresentedDeviceCertificate(device.Id, pinned, null, SameAgainMessage)
                : new PresentedDeviceCertificate(device.Id, pinned, presented);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PresentedDeviceCertificate(device.Id, pinned, null, "Unreachable - the device did not answer in time");
        }
#pragma warning disable CA1031 // Every device error becomes this device's result; the other devices go on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return new PresentedDeviceCertificate(device.Id, pinned, null, DeviceLoginService.Classify(device.Id, ex).Message);
        }
    }

    private async Task<DeviceCertificateTrustResult> TrustOneAsync(Device device, string fingerprint, CancellationToken ct)
    {
        if (device.Status != DeviceStatus.CertificateChanged || device.Scheme != DeviceScheme.Https)
        {
            return new DeviceCertificateTrustResult(device.Id, false, NotChangedMessage);
        }

        try
        {
            await addresses.UpdateTlsAsync(device.Id, Uri.UriSchemeHttps, fingerprint, ct).ConfigureAwait(false);
            clients.Invalidate(device.Id);
            LogTrusted(device.Serial, device.Address, fingerprint);
            return new DeviceCertificateTrustResult(device.Id, true);
        }
        catch (Exception ex) when (ex is CertificateChangedException
            || (ex is DeviceIdentityException && ex.Message.Contains("expected certificate", StringComparison.Ordinal)))
        {
            return new DeviceCertificateTrustResult(device.Id, false, OtherCertificateMessage);
        }
        catch (DeviceIdentityException)
        {
            return new DeviceCertificateTrustResult(device.Id, false, DeviceLoginService.OtherDeviceMessage);
        }
        catch (KeyNotFoundException)
        {
            return new DeviceCertificateTrustResult(device.Id, false, DeviceMessages.Removed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeviceCertificateTrustResult(device.Id, false, "Unreachable - the device did not answer in time");
        }
#pragma warning disable CA1031 // Every device error becomes this device's result; the other devices go on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var message = DeviceLoginService.Classify(device.Id, ex).Message;
            LogTrustFailed(device.Serial, device.Address, message ?? ex.Message);
            return new DeviceCertificateTrustResult(device.Id, false, message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trusted the new certificate of device {Serial} at {Address}: {Fingerprint}")]
    private partial void LogTrusted(string serial, string address, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trusting the new certificate of device {Serial} at {Address} failed: {Reason}")]
    private partial void LogTrustFailed(string serial, string address, string reason);
}
