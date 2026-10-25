using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>A managed device as plugins see it; every value can be set (<c>new FakeDevice { Status = ... }</c>).</summary>
public sealed record FakeDevice : IDeviceInfo
{
    /// <inheritdoc />
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <inheritdoc />
    public string Serial { get; init; } = "B8A44F631339";

    /// <inheritdoc />
    public string Address { get; init; } = "192.0.2.10";

    /// <inheritdoc />
    public string? HostName { get; init; }

    /// <inheritdoc />
    public string? Model { get; init; } = "P3265-V";

    /// <inheritdoc />
    public string? FirmwareVersion { get; init; } = "12.11.77";

    /// <inheritdoc />
    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;

    /// <inheritdoc />
    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;

    /// <inheritdoc />
    public bool HasVideo => Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;

    /// <inheritdoc />
    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];

    /// <inheritdoc />
    public string? CredentialUserName { get; init; } = "root";

    /// <inheritdoc />
    public bool? DhcpEnabled { get; init; }

    /// <inheritdoc />
    public bool? HttpsEnabled { get; init; }

    /// <inheritdoc />
    public bool? ConnectsOverHttps { get; init; }

    /// <inheritdoc />
    public bool? Dot1xEnabled { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The device's site; null = the server's own network.</summary>
    public Guid? SiteId { get; init; }

    /// <summary>The name of <see cref="SiteId"/>.</summary>
    public string? SiteName { get; init; }

    /// <summary>A user with all devices added it (host names may be trusted). Default true.</summary>
    public bool AddedByAllDevices { get; init; } = true;

    /// <summary>End of validity (UTC) of the device's HTTPS certificate; null: HTTP only or not checked yet.</summary>
    public DateTime? CertNotAfterUtc { get; init; }
}
