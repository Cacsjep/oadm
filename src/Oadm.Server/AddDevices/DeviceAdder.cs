using System.Net;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Server.Discovery;
using Oadm.Server.Mapping;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>
/// Stores one discovered device as a managed device, shared by the add page (<see cref="AddDevicesGrpcService.Commit"/>)
/// and the automatic add of core plugins (<see cref="DeviceAutoAddService"/>): serial unique, address per
/// <see cref="ChooseAddress"/>, certificate pin from the probe, first root password on a factory-default device when one
/// is given (HTTPS when offered, POST body only), else the credential that worked, verified with one authenticated call
/// (wrong credentials still add the device, status Credentials required). The caller queues the first full refresh.
/// </summary>
public sealed partial class DeviceAdder(
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    IVapixConnector connector,
    VapixProbe probe,
    ILogger<DeviceAdder> logger)
{
    /// <summary>
    /// Adds the device. <paramref name="probed"/> is the anonymous probe the caller already ran (null: probed here).
    /// Returns null when the serial is managed already.
    /// </summary>
    public async Task<(Guid Id, SdkDeviceStatus Status)?> AddAsync(
        DiscoveredDevice found,
        bool useHostName,
        string? initialPassword,
        DeviceCredentials? deviceCredentials,
        string serverName,
        CancellationToken ct,
        VapixProbeResult? probed = null)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (await devices.FindBySerialAsync(found.Serial, ct).ConfigureAwait(false) is not null)
        {
            LogSkippedManaged(found.Serial);
            return null;
        }

        probed ??= await TryProbeAsync(found, ct).ConfigureAwait(false);
        var (address, hostName) = ChooseAddress(found, useHostName);
        var scheme = probed?.Scheme ?? found.Scheme ?? Uri.UriSchemeHttps;
        var factoryDefault = IsFactoryDefault(found, probed);

        Device device;
        try
        {
            device = await devices.AddAsync(
                new Device
                {
                    Serial = found.Serial,
                    Address = address,
                    UseHostName = useHostName,
                    HostName = hostName,
                    Model = probed?.Model ?? found.Model,
                    FirmwareVersion = probed?.FirmwareVersion ?? found.FirmwareVersion,
                    ProductType = probed?.ProductType ?? found.ProductType,
                    Category = DeviceCategoryMapper.Map(probed?.ProductType ?? found.ProductType),
                    Scheme = Mappers.ToDeviceScheme(scheme),
                    CertFingerprintSha256 = scheme == Uri.UriSchemeHttps ? probed?.CertificateFingerprint : null,
                    ServerName = serverName,
                    Status = probed is null ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.Unknown,
                },
                ct).ConfigureAwait(false);
        }
        catch (DuplicateDeviceException)
        {
            LogSkippedManaged(found.Serial);
            return null;
        }

        SdkDeviceStatus status;
        if (factoryDefault)
        {
            status = initialPassword is null
                ? SdkDeviceStatus.PasswordNotSet
                : await SetInitialPasswordAsync(device, initialPassword, ct).ConfigureAwait(false);
        }
        else if (deviceCredentials is not null)
        {
            await credentials.SetAsync(device.Id, deviceCredentials.UserName.Trim(), deviceCredentials.Password, ct).ConfigureAwait(false);
            status = await VerifyAsync(device.Id, ct).ConfigureAwait(false);
        }
        else
        {
            status = probed is null ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.CredentialsRequired;
        }

        await devices.UpdateAsync(device.Id, d => d.Status = status, ct).ConfigureAwait(false);
        LogAdded(device.Serial, device.Address, status);
        return (device.Id, status);
    }

    /// <summary>The anonymous probe of a discovered device; null when it does not answer or another serial answers.</summary>
    public async Task<VapixProbeResult?> TryProbeAsync(DiscoveredDevice found, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(found);
        try
        {
            string[]? schemes = found.EnteredAddress is not null && found.Scheme is not null ? [found.Scheme] : null;
            var result = await probe.ProbeAsync(found.ConnectAddress, ct, schemes).ConfigureAwait(false);
            if (result is not null && !string.Equals(result.Serial, found.Serial, StringComparison.OrdinalIgnoreCase))
            {
                LogSerialMismatch(found.ConnectAddress, found.Serial, result.Serial);
                return null;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogProbeFailed(found.ConnectAddress, ex.Message);
            return null;
        }
    }

    public static bool IsFactoryDefault(DiscoveredDevice found, VapixProbeResult? probed)
    {
        ArgumentNullException.ThrowIfNull(found);
        return probed?.IsFactoryDefault ?? found.Status == DiscoveredDeviceStatus.PasswordNotSet;
    }

    /// <summary>The connection address: the entered address of "Add manually", else the host name when asked for and known, else the IP.</summary>
    public static (string Address, string? HostName) ChooseAddress(DiscoveredDevice found, bool useHostName)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (!string.IsNullOrWhiteSpace(found.EnteredAddress))
        {
            var host = EnteredAddress.TryParse(found.EnteredAddress, out var entered, out _) ? entered!.Host : null;
            var isName = host is not null && !IPAddress.TryParse(host, out _);
            return (found.EnteredAddress, isName ? host : found.HostName);
        }

        var hostName = found.HostName;
        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.Contains('.', StringComparison.Ordinal))
        {
            hostName += ".local"; // mDNS names come without the domain
        }

        var address = useHostName && !string.IsNullOrWhiteSpace(hostName) ? hostName : found.Address.ToString();
        return (address, string.IsNullOrWhiteSpace(hostName) ? null : hostName);
    }

    private async Task<SdkDeviceStatus> SetInitialPasswordAsync(Device device, string password, CancellationToken ct)
    {
        var scheme = Mappers.ToSchemeString(device.Scheme);
        try
        {
            using (var anonymous = connector.Connect(new VapixConnectionOptions
            {
                Address = device.Address,
                Scheme = scheme,
                PinnedCertificateFingerprint = device.CertFingerprintSha256,
            }))
            {
                // HTTPS when the device offers it (the probe tries it first); plain HTTP only as a fallback.
                await anonymous.SetInitialRootPasswordAsync(password, ct, allowPlainHttp: scheme == Uri.UriSchemeHttp).ConfigureAwait(false);
            }

            await credentials.SetAsync(device.Id, "root", password, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogInitialPasswordFailed(device.Serial, ex.Message);
            var status = DeviceStatusClassifier.FromException(ex);
            return status is SdkDeviceStatus.Unreachable or SdkDeviceStatus.CertificateChanged ? status : SdkDeviceStatus.PasswordNotSet;
        }

        return await VerifyAsync(device.Id, ct).ConfigureAwait(false);
    }

    /// <summary>One authenticated call with the stored credentials.</summary>
    private async Task<SdkDeviceStatus> VerifyAsync(Guid deviceId, CancellationToken ct)
    {
        try
        {
            var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
            await client.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            return SdkDeviceStatus.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DeviceStatusClassifier.FromException(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added device {Serial} at {Address}: {Status}")]
    private partial void LogAdded(string serial, string address, SdkDeviceStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} is already managed, skipped")]
    private partial void LogSkippedManaged(string serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the initial password on {Serial} failed: {Reason}")]
    private partial void LogInitialPasswordFailed(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe of {Address} failed: {Reason}")]
    private partial void LogProbeFailed(string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Address {Address} now answers as {Actual}, expected {Expected}")]
    private partial void LogSerialMismatch(string address, string expected, string actual);
}
