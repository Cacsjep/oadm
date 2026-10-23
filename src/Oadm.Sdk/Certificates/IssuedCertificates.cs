using System.Globalization;

namespace Oadm.Sdk.Certificates;

/// <summary>Why a certificate was revoked (SDK 0.4.0). The names are the page's choices.</summary>
public enum RevocationReason
{
    /// <summary>Another reason.</summary>
    Other = 0,

    /// <summary>The private key may be known to someone else (a stolen or opened device, a copied certificate).</summary>
    KeyCompromised = 1,

    /// <summary>The device was replaced by another one.</summary>
    DeviceReplaced = 2,

    /// <summary>The device was taken out of the system.</summary>
    DeviceRemoved = 3,
}

/// <summary>What the list of issued certificates says about one certificate (SDK 0.4.0).</summary>
public enum IssuedCertificateState
{
    /// <summary>Valid now, more than the warning days left.</summary>
    Valid = 0,

    /// <summary>Valid now, ends within the warning days (30 by default).</summary>
    ExpiresSoon = 1,

    /// <summary>Its validity ended.</summary>
    Expired = 2,

    /// <summary>A newer certificate of the same device and use exists.</summary>
    Replaced = 3,

    /// <summary>On the revocation list: no longer accepted, also while it is still valid.</summary>
    Revoked = 4,
}

/// <summary>The uses of device certificates in the list of issued certificates (SDK 0.4.0), and their plain names.</summary>
public static class DeviceCertificatePurposes
{
    /// <summary>The device's web server (server certificate).</summary>
    public const string Https = "https";

    /// <summary>Network access with IEEE 802.1X (client certificate).</summary>
    public const string Dot1x = "dot1x";

    /// <summary>MQTT over TLS (client certificate).</summary>
    public const string Mqtt = "mqtt";

    /// <summary>RTSP over TLS (server certificate).</summary>
    public const string Rtsps = "rtsps";

    /// <summary>Every use, in the page's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Https, Dot1x, Mqtt, Rtsps];

    /// <summary>"HTTPS", "IEEE 802.1X", "MQTT", "RTSPS"; the code itself for anything else.</summary>
    public static string Text(string? purpose) => purpose switch
    {
        Https => "HTTPS",
        Dot1x => "IEEE 802.1X",
        Mqtt => "MQTT",
        Rtsps => "RTSPS",
        null => string.Empty,
        _ => purpose,
    };

    /// <summary>The use a request names, else the one its <see cref="DeviceCertificateUse"/> stands for (HTTPS or IEEE 802.1X).</summary>
    public static string Of(DeviceCertificateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Purpose is { Length: > 0 } purpose && All.Contains(purpose, StringComparer.Ordinal)
            ? purpose
            : request.Use == DeviceCertificateUse.Client ? Dot1x : Https;
    }
}

/// <summary>Who asked the OADM CA for a certificate (SDK 0.4.0).</summary>
public static class IssuedCertificateSources
{
    /// <summary>A PKI task (Enable HTTPS, Enable IEEE 802.1X, Renew certificates).</summary>
    public const string Task = "Task";

    /// <summary>The camera itself through the EST server.</summary>
    public const string Est = "EST";

    /// <summary>The page's names ("Task", "EST").</summary>
    public static IReadOnlyList<string> All { get; } = [Task, Est];
}

/// <summary>Serial numbers of certificates in one form (SDK 0.4.0): upper-case hex, no separators, no leading zeros.</summary>
public static class CertificateSerials
{
    /// <summary>
    /// "00:7d:fd 05" and "7DFD05" both become "7DFD05" (.NET keeps a leading zero byte, BouncyCastle drops it); "0" for
    /// zero; empty for null or empty.
    /// </summary>
    public static string Normalize(string? serialNumber)
    {
        if (string.IsNullOrEmpty(serialNumber))
        {
            return string.Empty;
        }

        Span<char> buffer = serialNumber.Length <= 256 ? stackalloc char[serialNumber.Length] : new char[serialNumber.Length];
        var length = 0;
        foreach (var c in serialNumber)
        {
            if (c is ' ' or ':' or '-')
            {
                continue;
            }

            if (length == 0 && c == '0')
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(c);
        }

        return length == 0 ? "0" : new string(buffer[..length]);
    }
}

/// <summary>One revoked certificate (SDK 0.4.0): kept while the certificate could still be valid.</summary>
/// <param name="SerialNumber">The serial number (<see cref="CertificateSerials.Normalize"/>).</param>
/// <param name="RevokedUtc">When it was revoked.</param>
/// <param name="Reason">Why.</param>
/// <param name="UserName">Who revoked it.</param>
public sealed record CertificateRevocation(string SerialNumber, DateTime RevokedUtc, RevocationReason Reason, string UserName)
{
    /// <summary>The device the certificate was issued for; null when not known.</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>The end of the certificate's validity: the entry leaves the list after it.</summary>
    public DateTime NotAfterUtc { get; init; }

    /// <summary>"Key compromised", "Device replaced", "Device removed", "Other".</summary>
    public static string ReasonText(RevocationReason reason) => reason switch
    {
        RevocationReason.KeyCompromised => "Key compromised",
        RevocationReason.DeviceReplaced => "Device replaced",
        RevocationReason.DeviceRemoved => "Device removed",
        _ => "Other",
    };

    /// <summary>"Certificate revoked on 2026-11-02 (Key compromised)": the text of a refusal (802.1X log, EST).</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"Certificate revoked on {RevokedUtc:yyyy-MM-dd} ({ReasonText(Reason)})");
}

/// <summary>One certificate the OADM CA issued for a device (SDK 0.4.0), as the list of issued certificates shows it.</summary>
public sealed record IssuedCertificateInfo
{
    /// <summary>The serial number (<see cref="CertificateSerials.Normalize"/>).</summary>
    public string SerialNumber { get; init; } = string.Empty;

    /// <summary>The device it was issued for.</summary>
    public Guid DeviceId { get; init; }

    /// <summary>The device's serial (from its <c>axis-&lt;serial&gt;.local</c> name); empty when the certificate names none.</summary>
    public string DeviceSerial { get; init; } = string.Empty;

    /// <summary>The device's address as the device list shows it; empty for a device OADM no longer manages.</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>The device is still managed.</summary>
    public bool DeviceManaged { get; init; }

    /// <summary>One of <see cref="DeviceCertificatePurposes"/>.</summary>
    public string Purpose { get; init; } = DeviceCertificatePurposes.Https;

    /// <summary>One of <see cref="IssuedCertificateSources"/>.</summary>
    public string Source { get; init; } = IssuedCertificateSources.Task;

    /// <summary>Start of validity.</summary>
    public DateTime NotBeforeUtc { get; init; }

    /// <summary>End of validity.</summary>
    public DateTime NotAfterUtc { get; init; }

    /// <summary>When OADM issued it (or found it on the device, for certificates from before the list).</summary>
    public DateTime IssuedUtc { get; init; }

    /// <summary>Its state now.</summary>
    public IssuedCertificateState State { get; init; }

    /// <summary>The revocation, when it is revoked.</summary>
    public CertificateRevocation? Revocation { get; init; }
}

/// <summary>A query of the list of issued certificates (SDK 0.4.0): filters, search and one page, newest first.</summary>
public sealed record IssuedCertificateQuery
{
    /// <summary>The most rows of one page.</summary>
    public const int MaxLimit = 500;

    /// <summary>Only this state; null = every state.</summary>
    public IssuedCertificateState? State { get; init; }

    /// <summary>Only this use (<see cref="DeviceCertificatePurposes"/>); null = every use.</summary>
    public string? Purpose { get; init; }

    /// <summary>Only this source (<see cref="IssuedCertificateSources"/>); null = every source.</summary>
    public string? Source { get; init; }

    /// <summary>Text in the device's address, host name or serial, or in the certificate's serial number; case-insensitive.</summary>
    public string? Search { get; init; }

    /// <summary>Only certificates of these devices (a user limited to a device group); null = every device.</summary>
    public IReadOnlySet<Guid>? DeviceIds { get; init; }

    /// <summary>Rows to skip.</summary>
    public int Offset { get; init; }

    /// <summary>Rows of the page, 1 to <see cref="MaxLimit"/>.</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>One page of <see cref="IssuedCertificateQuery"/>.</summary>
/// <param name="Rows">The certificates of the page, newest first.</param>
/// <param name="Total">How many match the filters and the search.</param>
public sealed record IssuedCertificatePage(IReadOnlyList<IssuedCertificateInfo> Rows, int Total)
{
    /// <summary>No rows.</summary>
    public static IssuedCertificatePage Empty { get; } = new([], 0);
}

/// <summary>The result of <see cref="IDeviceCertificateAuthority.RevokeAsync"/>.</summary>
/// <param name="Revoked">Newly revoked.</param>
/// <param name="AlreadyRevoked">Were revoked before (a revocation is never changed).</param>
/// <param name="NotFound">Not in the list of issued certificates, or already expired (no need to revoke).</param>
public sealed record RevokeResult(int Revoked, int AlreadyRevoked, int NotFound);
