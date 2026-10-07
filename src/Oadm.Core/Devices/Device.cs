using Oadm.Core.Vapix;
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

    /// <summary>Raw basicdeviceinfo ProdType, e.g. "Dome Camera", "Network Speaker".</summary>
    public string? ProductType { get; set; }

    /// <summary>Category mapped from <see cref="ProductType"/> by <see cref="DeviceCategoryMapper"/>.</summary>
    public DeviceCategory Category { get; set; } = DeviceCategory.Unknown;

    /// <summary>Derived from <see cref="Category"/>; not stored.</summary>
    public bool HasVideo => DeviceCategories.HasVideo(Category);

    /// <summary>
    /// VAPIX API list (<c>apidiscovery.cgi getApiList</c>) from the last full refresh, stored as a JSON
    /// column. Empty until the first successful full refresh. Plugins check it in <c>CanRun</c>.
    /// </summary>
    public IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> Apis { get; set; } = [];

    /// <summary>
    /// User name of the stored credentials (never the password); not a column. Filled by
    /// <see cref="DeviceRepository"/> reads from the DeviceCredentials table, null when none are stored.
    /// </summary>
    public string? CredentialUserName { get; set; }

    /// <summary>End of validity of the device HTTPS certificate (UTC), null for HTTP-only or not yet checked.</summary>
    public DateTime? CertNotAfterUtc { get; set; }

    /// <summary>Chain trust of the HTTPS certificate (chain only, host name ignored). Unknown for HTTP-only.</summary>
    public CertificateTrust CertTrust { get; set; } = CertificateTrust.Unknown;

    /// <inheritdoc />
    string? IDeviceInfo.CertTrustName => CertTrust == CertificateTrust.Unknown ? null : CertTrust.ToString();

    /// <summary>Subject distinguished name of the HTTPS certificate.</summary>
    public string? CertSubject { get; set; }

    /// <summary>Issuer distinguished name of the HTTPS certificate.</summary>
    public string? CertIssuer { get; set; }

    /// <summary>The address we connect to is in the certificate SAN. Stored for later use, not shown yet.</summary>
    public bool? CertNameMatches { get; set; }

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
        copy.Apis = [.. Apis];
        return copy;
    }

    public override string ToString() => $"Device {Serial} ({Address})";
}
