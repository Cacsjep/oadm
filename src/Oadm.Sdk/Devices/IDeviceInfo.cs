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
