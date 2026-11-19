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

    /// <summary>The server manages <see cref="DeviceLimits.MaxDevices"/> devices already; nothing was added.</summary>
    LimitReached = 7,
}

/// <summary>What <see cref="IDeviceAutoAdd.AddAsync"/> did with a device.</summary>
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
    /// <param name="address">The IP address the device was seen at.</param>
    /// <param name="expectedSerial">The serial number (MAC address) the device must report.</param>
    /// <param name="source">Who found it, for the log and the audit entry ("DHCP server").</param>
    /// <param name="ct">Cancels the checks and the login.</param>
    Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct);

    /// <summary>
    /// The managed device with serial number <paramref name="serial"/> was seen at <paramref name="address"/>: when the
    /// record has another IP address, verify the device there and move the record (devices reached by host name keep it).
    /// </summary>
    Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct);

    /// <summary>
    /// SDK 0.15.0: what answers at <paramref name="address"/>, without a password: the add page's anonymous probe and Axis
    /// check, and whether it is factory-new (no password yet), always asked of the device itself, also for a managed one (a
    /// camera reset to factory default after it was added is new again). A device that is still starting is asked again
    /// until it says whether it needs setup, at most 2 minutes (then <see cref="DeviceLookResult.Starting"/>). Never throws
    /// for device errors. Older hosts: <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="address">The IP address the device was seen at.</param>
    /// <param name="expectedSerial">The serial number (MAC address) the device must report.</param>
    /// <param name="ct">Cancels the checks.</param>
    Task<DeviceLook> LookAsync(string address, string expectedSerial, CancellationToken ct) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>
    /// SDK 0.15.0: sets the first root password of a factory-new device, exactly like the add page and the context menu's
    /// "Set password" (HTTPS when the device offers it, POST body only). Right before writing, the device at the address must
    /// answer with <paramref name="expectedSerial"/> and still need its first password; otherwise nothing is sent. A managed
    /// device keeps the password as its login. The password comes from the credential list (never through the plugin).
    /// Never throws for device errors. Older hosts: <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="address">The IP address of the device.</param>
    /// <param name="expectedSerial">The serial number (MAC address) the device must report.</param>
    /// <param name="credentialId">The credential list entry whose password is set (its user must be root).</param>
    /// <param name="source">Who sets it, for the log and the audit entry ("Provisioning").</param>
    /// <param name="ct">Cancels the request.</param>
    Task<DeviceFirstPasswordOutcome> SetFirstPasswordAsync(string address, string expectedSerial, Guid credentialId, string source, CancellationToken ct) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>
    /// SDK 0.15.0: adds the device at <paramref name="address"/> like <see cref="AddAsync(string, string, string, CancellationToken)"/>
    /// (Axis check first), logged in with one credential list entry instead of trying the list, with <paramref name="tags"/>
    /// for all devices (so it lands in the device groups of those tags). A device OADM manages already keeps its record and
    /// gets the tags (<see cref="DeviceAutoAddResult.AlreadyManaged"/> with its id). Never throws for device errors. Older
    /// hosts: <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="address">The IP address of the device.</param>
    /// <param name="expectedSerial">The serial number (MAC address) the device must report.</param>
    /// <param name="credentialId">The credential list entry to log in with.</param>
    /// <param name="tags">Tags the device gets (names; a tag that does not exist yet is created).</param>
    /// <param name="source">Who adds it, for the log and the audit entry.</param>
    /// <param name="ct">Cancels the checks and the login.</param>
    Task<DeviceAutoAddOutcome> AddWithLoginAsync(string address, string expectedSerial, Guid credentialId, IReadOnlyList<string> tags, string source, CancellationToken ct) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>
    /// SDK 0.15.0: tells the plugin about managed devices that need their first password (status Password not set): a device
    /// added that way (the add page, an import, the waiting list, the automatic add) and a managed device whose status turned
    /// Password not set (reset to factory default). Called on the thread that stored the device: queue the work, never block.
    /// Dispose the answer to stop. Older hosts: <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="found">Called once per device and change.</param>
    IDisposable WatchFactoryNew(Action<FactoryNewDevice> found) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>SDK 0.15.0: the entries of the credential list, without passwords, in the order they are tried.</summary>
    /// <param name="ct">Cancels the read.</param>
    Task<IReadOnlyList<DeviceCredentialName>> ListCredentialsAsync(CancellationToken ct) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

    /// <summary>
    /// SDK 0.15.0: adds a user name and password to the credential list (encrypted with the server's key; an equal entry is
    /// reused) and returns its entry. The password is never logged. <see cref="ArgumentException"/> for a password the
    /// devices do not take, <see cref="InvalidOperationException"/> when the list is full.
    /// </summary>
    /// <param name="userName">The user name ("root").</param>
    /// <param name="password">The password, 1 to 64 printable ASCII characters.</param>
    /// <param name="ct">Cancels the write.</param>
    Task<DeviceCredentialName> AddCredentialAsync(string userName, string password, CancellationToken ct) =>
        throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
}

/// <summary>What answers at an address, without a password (<see cref="IDeviceAutoAdd.LookAsync"/>, SDK 0.15.0).</summary>
public enum DeviceLookResult
{
    /// <summary>An Axis device with the expected serial number that has no password yet (factory default).</summary>
    FactoryNew = 0,

    /// <summary>An Axis device with the expected serial number that has a password already.</summary>
    HasPassword = 1,

    /// <summary>The address did not identify itself as an Axis device.</summary>
    NotAxis = 2,

    /// <summary>An Axis device answers, but with another serial number.</summary>
    SerialMismatch = 3,

    /// <summary>Nothing answered (the device may still be starting).</summary>
    Unreachable = 4,

    /// <summary>An Axis device with the expected serial number answers, but it was still starting after 2 minutes: unknown whether it has a password.</summary>
    Starting = 5,
}

/// <summary>A managed device that needs its first password (<see cref="IDeviceAutoAdd.WatchFactoryNew"/>, SDK 0.15.0).</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Serial">Its serial number (MAC address, 12 hex digits).</param>
/// <param name="Address">Its address.</param>
/// <param name="AtSite">It is at a site (reached through a site node).</param>
/// <param name="Added">It was just added (false: a managed device turned Password not set).</param>
public sealed record FactoryNewDevice(Guid DeviceId, string Serial, string Address, bool AtSite, bool Added)
{
    /// <summary>
    /// A managed device that had a password when OADM last reached it (reset to factory default since); false when it was
    /// just added or OADM does not know (it was offline since the server started).
    /// </summary>
    public bool HadPassword { get; init; }
}

/// <summary>The answer of <see cref="IDeviceAutoAdd.LookAsync"/> (SDK 0.15.0).</summary>
/// <param name="Result">What answered.</param>
/// <param name="DeviceId">The managed device with this serial number; null when OADM does not manage it.</param>
/// <param name="Model">The product number the device reported ("P3265-LVE"), when known.</param>
/// <param name="Message">Plain-language text for pages and logs.</param>
public sealed record DeviceLook(DeviceLookResult Result, Guid? DeviceId, string? Model, string Message);

/// <summary>The answer of <see cref="IDeviceAutoAdd.SetFirstPasswordAsync"/> (SDK 0.15.0).</summary>
/// <param name="Ok">The device took the password.</param>
/// <param name="Message">Plain-language text: what was done, or why nothing was changed. Never the password.</param>
public sealed record DeviceFirstPasswordOutcome(bool Ok, string Message);

/// <summary>A credential list entry without its password (SDK 0.15.0).</summary>
/// <param name="Id">The entry's id.</param>
/// <param name="UserName">Its user name.</param>
/// <param name="Added">When it was added to the list (UTC); null when the host cannot tell. Tells equal user names apart.</param>
public sealed record DeviceCredentialName(Guid Id, string UserName, DateTimeOffset? Added = null);

/// <summary>The texts of the first set up of a device (SDK 0.15.0).</summary>
public static class DeviceFirstSetupTexts
{
    /// <summary>A host without the first set up of devices.</summary>
    public const string NotSupported = "This OADM server cannot set up new devices.";

    /// <summary>The credential list entry is gone.</summary>
    public const string CredentialGone = "The password is no longer in the credential list. Choose another one.";

    /// <summary>The first password is root's.</summary>
    public const string RootOnly = "The first password is the password of root. Choose a credential of the user root. Nothing was changed.";
}
