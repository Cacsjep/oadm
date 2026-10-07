using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// Lets running tasks follow a device to a new address (<c>ITaskExecutionContext.CreateClientForAsync</c> /
/// <c>UpdateDeviceAddressAsync</c>). The server implementation is <c>Oadm.Core.Devices.DeviceAddressService</c>.
/// </summary>
public interface ITaskDeviceAddresses
{
    /// <summary>A new client for the device at <paramref name="address"/> (stored credentials, scheme, pin). The caller disposes it.</summary>
    Task<IVapixClient> CreateClientAsync(Guid deviceId, string address, CancellationToken ct);

    /// <summary>
    /// Verifies the serial number at <paramref name="newAddress"/> and moves the device record there.
    /// Throws <c>DeviceIdentityException</c> when the device there is not the same one.
    /// </summary>
    Task<DeviceAddressChangeResult> UpdateAddressAsync(Guid deviceId, string newAddress, CancellationToken ct);

    /// <summary>
    /// Switches the device record to <paramref name="scheme"/> ("https" / "http") after verifying the serial number
    /// and, for https, that the device presents the certificate <paramref name="expectedFingerprintSha256"/>; stores the
    /// new pin and certificate details. Throws <c>DeviceIdentityException</c> otherwise (record unchanged).
    /// </summary>
    Task UpdateTlsAsync(Guid deviceId, string scheme, string? expectedFingerprintSha256, CancellationToken ct) =>
        throw new NotSupportedException("The device connection cannot be changed here.");
}

/// <summary>Outcome of a device address change.</summary>
public enum DeviceAddressChangeResult
{
    /// <summary>The record now has the new address.</summary>
    Updated = 0,

    /// <summary>The record already had that address.</summary>
    Unchanged = 1,

    /// <summary>OADM reaches the device by host name (Devices.UseHostName); the host name is kept.</summary>
    KeptHostName = 2,

    /// <summary>No managed device has that serial number.</summary>
    NotManaged = 3,

    /// <summary>The device is not unreachable at its stored address, so it is not moved.</summary>
    StillReachable = 4,

    /// <summary>The device at the announced address could not be verified (not answering or another serial).</summary>
    NotVerified = 5,
}
