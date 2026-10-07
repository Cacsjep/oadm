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
