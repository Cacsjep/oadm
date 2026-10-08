namespace Oadm.Sdk.Devices;

/// <summary>What happened when a core plugin asked the server to add a device it saw on the network.</summary>
public enum DeviceAutoAddResult
{
    /// <summary>Added; a credential of the credential list works (status Ok after the first refresh).</summary>
    Added = 0,

    /// <summary>Added without a working credential: status Credentials required (the context menu "Log in" fixes it).</summary>
    AddedCredentialsRequired = 1,

    /// <summary>Added in factory default: status Password not set (the context menu "Set password" fixes it).</summary>
    AddedPasswordNotSet = 2,

    /// <summary>OADM manages this serial number already; nothing was added.</summary>
    AlreadyManaged = 3,

    /// <summary>The address did not identify itself as an Axis device (anonymous check). No password was sent.</summary>
    NotAxis = 4,

    /// <summary>An Axis device answers at the address, but with another serial number than expected.</summary>
    SerialMismatch = 5,

    /// <summary>Nothing answered at the address (the device may still be starting).</summary>
    Unreachable = 6,
}

/// <param name="Result">The outcome.</param>
/// <param name="DeviceId">The new device record when one was added.</param>
/// <param name="Message">Plain-language text for logs and pages, never a password.</param>
public sealed record DeviceAutoAddOutcome(DeviceAutoAddResult Result, Guid? DeviceId, string Message)
{
    /// <summary>A device record was created.</summary>
    public bool IsAdded => Result is DeviceAutoAddResult.Added or DeviceAutoAddResult.AddedCredentialsRequired or DeviceAutoAddResult.AddedPasswordNotSet;
}

/// <summary>What happened when a core plugin reported a managed device at a (possibly) new address.</summary>
public enum DeviceFollowResult
{
    /// <summary>No managed device has this serial number.</summary>
    NotManaged = 0,

    /// <summary>The record already has this address.</summary>
    Unchanged = 1,

    /// <summary>The device was verified at the new address (serial number with the stored credentials) and its record moved.</summary>
    Moved = 2,

    /// <summary>OADM reaches the device by host name; the record keeps it.</summary>
    KeptHostName = 3,

    /// <summary>The device at the new address could not be verified (no answer yet, another serial); the record is unchanged.</summary>
    NotVerified = 4,
}

/// <summary>
/// Adds devices a core plugin saw on the network (the DHCP server's leases) through the same pipeline as the add page:
/// anonymous Axis check, factory default check, automatic login with the credential list (never passwords of managed
/// devices), then the device is stored like an add from the page and its first full refresh queued. Also follows
/// managed devices to a new address. Implemented by the server; passwords never reach the plugin.
/// </summary>
public interface IDeviceAutoAdd
{
    /// <summary>
    /// Adds the device at <paramref name="address"/> (an IP address) when it is an Axis device with serial number
    /// <paramref name="expectedSerial"/> (= MAC address) that OADM does not manage yet. Never throws for device errors.
    /// </summary>
    /// <param name="source">Who found it, for the log and the audit entry ("DHCP server").</param>
    Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct);

    /// <summary>
    /// The managed device with serial number <paramref name="serial"/> was seen at <paramref name="address"/>: when the
    /// record has another IP address, verify the device there and move the record (devices reached by host name keep it).
    /// </summary>
    Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct);
}
