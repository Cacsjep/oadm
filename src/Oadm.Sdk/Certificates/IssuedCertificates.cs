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

    /// <summary>
    /// Set by OADM itself (never a choice of the page): a newer certificate of the same camera and use was confirmed on the
    /// camera, or an issued certificate never reached it (a failed install, a newer one issued first).
    /// </summary>
    Superseded = 4,
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

    /// <summary>
    /// A newer certificate of the same device and use exists. Kept for compatibility: PKI lists only each camera's current
    /// certificates since OADM 4.5 and never answers this state.
    /// </summary>
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

/// <summary>
/// The uses of every certificate the PKI page lists (SDK 0.4.0): the device uses of <see cref="DeviceCertificatePurposes"/>
/// and OADM's own server certificates, CAs and the rest.
/// </summary>
public static class ListedCertificateUses
{
    /// <summary>A certificate of an OADM server (802.1X server, EST server).</summary>
    public const string Server = "server";

    /// <summary>A certificate authority.</summary>
    public const string Ca = "ca";

    /// <summary>Any other use.</summary>
    public const string Other = "other";

    /// <summary>Every use, in the page's order.</summary>
    public static IReadOnlyList<string> All { get; } = [.. DeviceCertificatePurposes.All, Server, Ca, Other];

    /// <summary>"HTTPS", "IEEE 802.1X", "MQTT", "RTSPS", "Server", "CA", "Other".</summary>
    public static string Text(string? use) => use switch
    {
        Server => "Server",
        Ca => "CA",
        Other => "Other",
        _ => DeviceCertificatePurposes.Text(use),
    };
}

/// <summary>What a row of the PKI page's certificate list is (SDK 0.4.0).</summary>
public enum ListedCertificateKind
{
    /// <summary>A device certificate the OADM CA issued (a task or EST).</summary>
    Device = 0,

    /// <summary>A certificate authority of PKI's list.</summary>
    Authority = 1,

    /// <summary>The certificate of an OADM server (802.1X server, EST server).</summary>
    Server = 2,

    /// <summary>OADM made the key and the certificate (Create).</summary>
    Created = 3,

    /// <summary>The OADM CA signed a request of another system (Sign request); OADM holds no key.</summary>
    SignedRequest = 4,

    /// <summary>Imported, with or without its key.</summary>
    Imported = 5,
}

/// <summary>A choice of the "Issued by" filter (SDK 0.4.0): the CA's id and its name.</summary>
/// <param name="Id">The CA's id (<see cref="CertificateAuthorityInfo.Id"/>), or "other" for every CA PKI does not list.</param>
/// <param name="Name">The CA's name.</param>
public sealed record CertificateIssuerChoice(string Id, string Name);

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

/// <summary>The answers about one certificate of the list of issued certificates (SDK 0.4.0), the same on every page and server.</summary>
public static class IssuedCertificateMessages
{
    /// <summary>A serial that is not listed, or a certificate of a device outside the user's group (the same answer).</summary>
    public const string NotListed = "This certificate is not in the list.";

    /// <summary>A certificate listed before OADM kept a copy of each one (4.1 and older), until a read of its device finds it.</summary>
    public const string NoCopy = "OADM keeps no copy of this certificate.";

    /// <summary>The issuer of a root CA and of the OADM server's own certificate in the list.</summary>
    public const string SelfSigned = "Self-signed";
}

/// <summary>One revoked certificate (SDK 0.4.0): kept while the certificate could still be valid.</summary>
/// <param name="SerialNumber">The serial number (<see cref="CertificateSerials.Normalize"/>).</param>
/// <param name="RevokedUtc">When it was revoked.</param>
/// <param name="Reason">Why.</param>
/// <param name="UserName">Who revoked it.</param>
public sealed record CertificateRevocation(string SerialNumber, DateTime RevokedUtc, RevocationReason Reason, string UserName)
{
    /// <summary>The device the certificate was issued for; null when not known (a serial the list did not hold).</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>The end of the certificate's validity: the entry leaves the list after it.</summary>
    public DateTime NotAfterUtc { get; init; }

    /// <summary>"Key compromised", "Device replaced", "Device removed", "Replaced by a newer certificate", "Other".</summary>
    public static string ReasonText(RevocationReason reason) => reason switch
    {
        RevocationReason.KeyCompromised => "Key compromised",
        RevocationReason.DeviceReplaced => "Device replaced",
        RevocationReason.DeviceRemoved => "Device removed",
        RevocationReason.Superseded => "Replaced by a newer certificate",
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

    /// <summary>The row's id when it is no device certificate ("ca:...", "local:...", "server:..."); null = <see cref="SerialNumber"/>.</summary>
    public string? Id { get; init; }

    /// <summary>What the row is.</summary>
    public ListedCertificateKind Kind { get; init; }

    /// <summary>The certificate's name for rows that are no device certificate; null = the device.</summary>
    public string? Name { get; init; }

    /// <summary>The name of the CA that issued it ("OADM Root CA SERVER01"); null = <see cref="Source"/>.</summary>
    public string? IssuedBy { get; init; }

    /// <summary>What uses it: the device it is installed on, "802.1X server", "EST server"; for a CA every policy and service; null = nothing.</summary>
    public string? UsedBy { get; init; }

    /// <summary>
    /// The short form of <see cref="UsedBy"/> as badges: a CA's groups ("802.1X", "EST", "Issuing"), a server certificate's
    /// server; null: <see cref="UsedBy"/> is shown as text (a device).
    /// </summary>
    public IReadOnlyList<string>? UsedByBadges { get; init; }

    /// <summary>OADM holds its private key (created, imported with its key): it can be exported as PKCS#12.</summary>
    public bool HasKey { get; init; }

    /// <summary>The id of the CA that issued it (<see cref="CertificateIssuerChoice.Id"/>, "other" for a CA PKI does not list); null when the server does not say.</summary>
    public string? IssuerId { get; init; }

    /// <summary>The key, "RSA 4096", "ECC P-256"; null when the server does not say (device certificates).</summary>
    public string? KeyText { get; init; }

    /// <summary>A CA row: the kind of its CA (<see cref="CertificateAuthorityKind"/>); null for every other row.</summary>
    public CertificateAuthorityKind? AuthorityKind { get; init; }

    /// <summary>A CA row: the id of its CA (<see cref="CertificateAuthorityInfo.Id"/>); null for every other row.</summary>
    public string? AuthorityId { get; init; }

    /// <summary>A CA row: a root CA (it issued itself); false for an intermediate CA and every other row.</summary>
    public bool IsRootCa { get; init; }

    /// <summary>A CA row: PKI holds the CA's key, so its policies can pick it as the CA that issues; false for every other row.</summary>
    public bool CanIssue { get; init; }

    /// <summary>A CA row: the names of the policies that use the CA (issuing, trusted, sign-in), in the policies' order; null for none.</summary>
    public IReadOnlyList<string>? UsedIn { get; init; }

    /// <summary>A CA row whose stored CA cannot be read (damaged): it says so and offers only Delete; false for every other row.</summary>
    public bool Unreadable { get; init; }
}

/// <summary>How the list of certificates is grouped (SDK 0.4.0): by type (the default), by the CA that issued them, or not.</summary>
public enum IssuedCertificateGrouping
{
    /// <summary>Certificate authorities, server certificates, the ones created, signed and imported, device certificates.</summary>
    Type = 0,

    /// <summary>One group per CA that issued them, in the order of the CAs, then "Other CA".</summary>
    Issuer = 1,

    /// <summary>One list.</summary>
    None = 2,
}

/// <summary>The groups of <see cref="IssuedCertificateGrouping.Type"/> (SDK 0.4.0): their keys and names.</summary>
public static class IssuedCertificateGroups
{
    /// <summary>The certificate authorities.</summary>
    public const string Authorities = "authorities";

    /// <summary>The certificates of the servers (802.1X server, EST server, the OADM server).</summary>
    public const string Servers = "servers";

    /// <summary>The certificates created, signed or imported on the PKI page.</summary>
    public const string Local = "local";

    /// <summary>The device certificates.</summary>
    public const string Devices = "devices";

    /// <summary>The group of a row by its kind.</summary>
    public static string OfKind(ListedCertificateKind kind) => kind switch
    {
        ListedCertificateKind.Authority => Authorities,
        ListedCertificateKind.Server => Servers,
        ListedCertificateKind.Device => Devices,
        _ => Local,
    };

    /// <summary>"Certificate authorities", "Server certificates", "Created, signed and imported", "Device certificates".</summary>
    public static string Title(string key) => key switch
    {
        Authorities => "Certificate authorities",
        Servers => "Server certificates",
        Local => "Created, signed and imported",
        _ => "Device certificates",
    };
}

/// <summary>One group of the list (SDK 0.4.0) as the server counted it over every page.</summary>
/// <param name="Key">The group's key: one of <see cref="IssuedCertificateGroups"/>, or the issuer's id.</param>
/// <param name="Title">The group's name ("Device certificates", "OADM Root CA SERVER01").</param>
/// <param name="Count">How many rows of every page are in it.</param>
public sealed record IssuedCertificateGroup(string Key, string Title, int Count);

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

    /// <summary>Only certificates the CA with this id issued (<see cref="CertificateIssuerChoice.Id"/>); null = every issuer.</summary>
    public string? IssuedBy { get; init; }

    /// <summary>Only certificates of these devices (a user limited to a device group); null = every device.</summary>
    public IReadOnlySet<Guid>? DeviceIds { get; init; }

    /// <summary>The order of the rows by group; a server without groups ignores it.</summary>
    public IssuedCertificateGrouping GroupBy { get; init; }

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
/// <param name="NotFound">Already expired (no need to revoke), or not a serial number. A serial the list does not hold is revoked.</param>
public sealed record RevokeResult(int Revoked, int AlreadyRevoked, int NotFound);
