using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>URL scheme that worked when talking to the device.</summary>
public enum DeviceScheme
{
    Https = 0,
    Http = 1,
}

/// <summary>A managed device (row of the Devices table).</summary>
public sealed class Device : IDeviceInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>MAC address, upper hex without separators. Unique. See <see cref="DeviceSerial"/>.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>IP address or host name used to reach the device.</summary>
    public string Address { get; set; } = string.Empty;

    public bool UseHostName { get; set; }
    public string? HostName { get; set; }

    /// <summary>ProdNbr from basicdeviceinfo.</summary>
    public string? Model { get; set; }

    public string? FirmwareVersion { get; set; }
    public bool? DhcpEnabled { get; set; }
    public bool? HttpsEnabled { get; set; }
    public bool? Dot1xEnabled { get; set; }
    public string? UpnpFriendlyName { get; set; }
    public string? ServerName { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public DeviceScheme Scheme { get; set; } = DeviceScheme.Https;

    /// <summary>TOFU certificate fingerprint (hex SHA-256), null for HTTP devices.</summary>
    public string? CertFingerprintSha256 { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Later goal.</summary>
    public DateOnly? WarrantyExpiry { get; set; }

    /// <summary>Later goal.</summary>
    public string? ReplacementModel { get; set; }

    /// <summary>Free-form tags, stored as a JSON array.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Detached deep copy, safe to hand to other threads.</summary>
    public Device Clone()
    {
        var copy = (Device)MemberwiseClone();
        copy.Tags = [.. Tags];
        return copy;
    }

    public override string ToString() => $"Device {Serial} ({Address})";
}
