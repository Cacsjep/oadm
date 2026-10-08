namespace Oadm.Sdk.Devices;

/// <summary>Read-only view of a managed device, as exposed to plugins.</summary>
public interface IDeviceInfo
{
    Guid Id { get; }
    string Serial { get; }
    string Address { get; }
    string? HostName { get; }
    string? Model { get; }
    string? FirmwareVersion { get; }
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
    Unknown = 0,
    Camera = 1,
    Encoder = 2,
    Speaker = 3,
    Audio = 4,
    Intercom = 5,
    Radar = 6,
    IoModule = 7,
    DoorController = 8,
    Other = 9,
}

public static class DeviceCategories
{
    /// <summary>Categories whose devices deliver video: Camera, Encoder, Intercom.</summary>
    public static bool HasVideo(DeviceCategory category) =>
        category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;
}

public enum DeviceStatus
{
    Unknown = 0,
    Ok = 1,
    Unreachable = 2,
    CredentialsRequired = 3,
    PasswordNotSet = 4,
    CertificateChanged = 5,
}

public interface IDeviceRepository
{
    Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct);
    Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct);
}
