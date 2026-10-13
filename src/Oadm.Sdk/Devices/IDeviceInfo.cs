namespace Oadm.Sdk.Devices;

/// <summary>Read-only view of a managed device, as exposed to plugins.</summary>
public interface IDeviceInfo
{
    /// <summary>OADM's id of the device: the key of every call and of a plugin's own data.</summary>
    Guid Id { get; }
    /// <summary>The serial number, which is the MAC address: upper case hex without separators ("B8A44F631339").</summary>
    string Serial { get; }
    /// <summary>The address OADM reaches the device at: an IP address or a DNS name, with a port when it is not the default.</summary>
    string Address { get; }
    /// <summary>The verified DNS name or the host name the device was added with; null when none is known.</summary>
    string? HostName { get; }
    /// <summary>The product number ("P3265-V"); null until the device was read.</summary>
    string? Model { get; }
    /// <summary>The AXIS OS version ("12.11.77"); null until the device was read.</summary>
    string? FirmwareVersion { get; }
    /// <summary>The status from the last poll (every 60 s by default). Check it before sending anything: see <see cref="DeviceMessages.Refusal"/>.</summary>
    DeviceStatus Status { get; }

    /// <summary>Kind of Axis device, mapped from basicdeviceinfo ProdType. Unknown until read.</summary>
    DeviceCategory Category { get; }

    /// <summary>The device produces video (camera, encoder, video intercom). Use it in <c>CanRun</c>, e.g. for snapshots.</summary>
    bool HasVideo { get; }

    /// <summary>VAPIX APIs and versions from the last full refresh; empty until known. See <c>DeviceApiExtensions</c>.</summary>
    IReadOnlyList<Vapix.DeviceApi> Apis { get; }

    /// <summary>
    /// User name of the credentials OADM stores for this device (never the password), null when none
    /// are stored or not known (client side). Plugins that manage users must never remove or demote it.
    /// Filled by the server for task execution, queries and <c>CanRun</c>.
    /// </summary>
    string? CredentialUserName => null;

    /// <summary>End of validity (UTC) of the device HTTPS certificate; null for HTTP-only devices or when not checked yet.</summary>
    DateTime? CertNotAfterUtc => null;

    /// <summary>
    /// Chain trust of the HTTPS certificate as shown in the device grid: "Trusted", "SelfSigned", "Untrusted" or
    /// "Expired"; null when unknown or HTTP only. Filled by the server.
    /// </summary>
    string? CertTrustName => null;

    /// <summary>The device gets its IPv4 address from DHCP (param <c>Network.BootProto</c>); null when not known. Filled by the server.</summary>
    bool? DhcpEnabled => null;

    /// <summary>HTTPS is enabled on the device (param <c>HTTPS.Enabled</c>); null when not known. Filled by the server.</summary>
    bool? HttpsEnabled => null;

    /// <summary>IEEE 802.1X is enabled on the wired interface (param <c>Network.Interface.I0.dot1x.Enabled</c>); null when not known. Filled by the server.</summary>
    bool? Dot1xEnabled => null;

    /// <summary>
    /// The device's tags (names, sorted, case-insensitive distinct), e.g. "Building A", "PTZ"; empty when none. Tags are
    /// set by the user in the Devices page (Tags dialog); colors are not part of the SDK. Filled on server and client.
    /// </summary>
    IReadOnlyList<string> Tags => [];
}

/// <summary>Kind of Axis device. Mapped from basicdeviceinfo ProdType ("Dome Camera", "Network Speaker", ...).</summary>
public enum DeviceCategory
{
    /// <summary>The product type was not read yet.</summary>
    Unknown = 0,
    /// <summary>A network camera.</summary>
    Camera = 1,
    /// <summary>A video encoder (analog cameras on a network).</summary>
    Encoder = 2,
    /// <summary>A network speaker or horn speaker.</summary>
    Speaker = 3,
    /// <summary>Other audio devices: microphones, amplifiers, audio bridges.</summary>
    Audio = 4,
    /// <summary>A door station or intercom.</summary>
    Intercom = 5,
    /// <summary>A security radar.</summary>
    Radar = 6,
    /// <summary>An I/O or relay module.</summary>
    IoModule = 7,
    /// <summary>A door controller.</summary>
    DoorController = 8,
    /// <summary>A device of a product type OADM does not know.</summary>
    Other = 9,
}

/// <summary>Facts derived from a <see cref="DeviceCategory"/>.</summary>
public static class DeviceCategories
{
    /// <summary>Categories whose devices deliver video: Camera, Encoder, Intercom.</summary>
    public static bool HasVideo(DeviceCategory category) =>
        category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;
}

/// <summary>Whether OADM can work with a device, from the polling.</summary>
public enum DeviceStatus
{
    /// <summary>Not polled yet, e.g. right after the server started.</summary>
    Unknown = 0,
    /// <summary>The device answers and accepts the stored login.</summary>
    Ok = 1,
    /// <summary>The device does not answer.</summary>
    Unreachable = 2,
    /// <summary>The device rejects the stored login (HTTP 401 or 403). The user fixes it with Log in.</summary>
    CredentialsRequired = 3,
    /// <summary>The device is in factory default and has no password yet. The user fixes it with Set password.</summary>
    PasswordNotSet = 4,
    /// <summary>The device presents another HTTPS certificate than the one OADM pinned. Nothing is sent to it.</summary>
    CertificateChanged = 5,
}

/// <summary>The managed devices, read-only (<see cref="Plugins.ICorePluginContext.Devices"/>).</summary>
public interface IDeviceRepository
{
    /// <summary>Every managed device as it is now.</summary>
    Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct);
    /// <summary>One device by its id; null when OADM does not manage it (any more).</summary>
    Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct);
}
