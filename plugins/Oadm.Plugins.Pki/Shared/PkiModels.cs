using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Network;

namespace Oadm.Plugins.Pki;

/// <summary>Ids, names and limits shared by the server part, the page and the tests.</summary>
public static class PkiPluginInfo
{
    public const string PluginId = "oadm.pki";
    public const string DisplayName = "PKI";
    public const string IconKey = "shield";

    /// <summary>Previous CAs kept (newest first).</summary>
    public const int MaxPreviousCas = 10;

    /// <summary>Largest import file (PKCS#12, PEM, key, RADIUS CA).</summary>
    public const int MaxImportBytes = 1024 * 1024;

    public const int DefaultValidityYears = 10;
    public const int MinValidityYears = 1;
    public const int MaxValidityYears = 30;

    /// <summary>RSA key size of generated CAs.</summary>
    public const int CaKeySize = 4096;

    public const int MaxNameLength = 64;

    /// <summary>Shortest backup password.</summary>
    public const int MinBackupPasswordLength = 8;

    /// <summary>Rows of one page of the Certificates tab.</summary>
    public const int CertificatesPageSize = 500;

    /// <summary>Most imported CAs (Certificate authorities card).</summary>
    public const int MaxImportedCas = 50;

    /// <summary>Most certificates of one Delete.</summary>
    public const int MaxRevokeAtOnce = 5000;

    /// <summary>"OADM Root CA &lt;server name&gt;".</summary>
    public static string DefaultCommonName(string serverName) => "OADM Root CA " + serverName;
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and the live event topic.</summary>
public static class PkiMethods
{
    /// <summary>() -> <see cref="PkiState"/>.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="CreateCaRequest"/> -> <see cref="PkiReply"/>: a new root or intermediate CA into the list of CAs.</summary>
    public const string CreateAuthority = "createAuthority";

    /// <summary>
    /// <see cref="DeleteAuthorityRequest"/> -> <see cref="PkiReply"/>: a CA out of the list (refused while a policy or a
    /// service names it or while a CA it signed is listed); its certificates stay.
    /// </summary>
    public const string DeleteAuthority = "deleteAuthority";

    /// <summary><see cref="ImportRequest"/> -> <see cref="PkiReply"/>: a CA with its key into the list of CAs (it never replaces the OADM CA).</summary>
    public const string Import = "import";

    /// <summary><see cref="ExportRequest"/> -> <see cref="FileReply"/>.</summary>
    public const string ExportPublic = "exportPublic";

    /// <summary><see cref="ExportPreviousRequest"/> -> <see cref="FileReply"/>.</summary>
    public const string ExportPrevious = "exportPrevious";

    /// <summary><see cref="BackupRequest"/> -> <see cref="FileReply"/> (PKCS#12 with the key).</summary>
    public const string Backup = "backup";

    /// <summary><see cref="InstallTrustRequest"/> (none: the first-start root CA) -> <see cref="InstallReply"/>: a root CA into the server's machine root store.</summary>
    public const string InstallServerTrust = "installServerTrust";

    /// <summary>Event: <see cref="PkiState"/> after every change.</summary>
    public const string StateTopic = "state";

    /// <summary><see cref="CertificatesRequest"/> -> <see cref="CertificatesReply"/>: one page of the issued certificates.</summary>
    public const string GetCertificates = "getCertificates";

    /// <summary><see cref="CertificateDetailsRequest"/> -> <see cref="CertificateDetailsReply"/>: one issued certificate in plain names.</summary>
    public const string GetCertificate = "getCertificate";

    /// <summary>Event (payload <c>{}</c>): the issued certificates or the revocation list changed; pages read their page again.</summary>
    public const string CertificatesTopic = "certificates";

    /// <summary><see cref="ImportAuthorityRequest"/> -> <see cref="PkiReply"/>: a CA certificate without key into the list of CAs.</summary>
    public const string ImportAuthority = "importAuthority";
}

/// <summary>The payload of <see cref="PkiMethods.GetCertificates"/>; filters as in <see cref="Oadm.Sdk.Certificates.IssuedCertificateQuery"/>.</summary>
public sealed record CertificatesRequest
{
    /// <summary>The CA rows are left out (the Certificates card; the CAs have their own card).</summary>
    public bool NoAuthorities { get; init; }

    public Oadm.Sdk.Certificates.IssuedCertificateState? State { get; init; }

    public string? Purpose { get; init; }

    public string? Source { get; init; }

    /// <summary>Only certificates of this CA (<see cref="Oadm.Sdk.Certificates.CertificateIssuerChoice.Id"/>).</summary>
    public string? IssuedBy { get; init; }

    public string? Search { get; init; }

    /// <summary>The order of the rows by group (type, issuer or none); the reply counts the groups.</summary>
    public Oadm.Sdk.Certificates.IssuedCertificateGrouping GroupBy { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = PkiPluginInfo.CertificatesPageSize;
}

/// <summary>One page of the issued certificates, newest first.</summary>
public sealed record CertificatesReply
{
    public IReadOnlyList<Oadm.Sdk.Certificates.IssuedCertificateInfo> Rows { get; init; } = [];

    /// <summary>How many match.</summary>
    public int Total { get; init; }

    /// <summary>The first row's position (the request's, or the last page's when fewer match now).</summary>
    public int Offset { get; init; }

    /// <summary>
    /// The days before the end that count as "Expires soon"; null since each device follows its Expiry warning policy (the
    /// certificate window then follows the row's state).
    /// </summary>
    public int? WarningDays { get; init; }

    /// <summary>The choices of the "Issued by" filter: the CAs of PKI and "Other CA".</summary>
    public IReadOnlyList<Oadm.Sdk.Certificates.CertificateIssuerChoice> Issuers { get; init; } = [];

    /// <summary>The groups of every page in their order with their counts (none without a grouping or without rows).</summary>
    public IReadOnlyList<Oadm.Sdk.Certificates.IssuedCertificateGroup> Groups { get; init; } = [];
}

/// <summary>The payload of <see cref="PkiMethods.GetCertificate"/>: a serial number, or the id of a row that is no device certificate.</summary>
public sealed record CertificateDetailsRequest(string SerialNumber);

/// <summary>One issued certificate; <see cref="Error"/> when it is not listed (or not one of the user's devices).</summary>
public sealed record CertificateDetailsReply
{
    public Oadm.Sdk.Certificates.CertificateDescription? Certificate { get; init; }

    public Oadm.Sdk.Certificates.CertificateRevocation? Revocation { get; init; }

    public string? Error { get; init; }
}

/// <summary>EAP identity choices of 802.1X.</summary>
public static class Dot1xIdentity
{
    public const string Mac = "mac";
    public const string HostName = "hostName";

    /// <summary>The device's IP address ("10.0.0.48"; decision 2026-10-25).</summary>
    public const string IpAddress = "ipAddress";

    /// <summary>The last octet of the device's IPv4 address ("48"; the last group of an IPv6 address; decision 2026-10-25).</summary>
    public const string IpLastOctet = "ipLastOctet";

    public const string Custom = "custom";

    /// <summary>Every mode, in the choice's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Mac, HostName, IpAddress, IpLastOctet, Custom];
}

/// <summary>Which CA the devices use to check the RADIUS server.</summary>
public static class RadiusCaSource
{
    public const string Oadm = "oadm";
    public const string Imported = "imported";
}

/// <summary>Where the active CA came from.</summary>
public static class CaSource
{
    public const string Generated = "generated";
    public const string Imported = "imported";
}

/// <summary>Purpose of an issued device certificate.</summary>
public static class CertificatePurpose
{
    public const string Https = Oadm.Sdk.Certificates.DeviceCertificatePurposes.Https;
    public const string Dot1x = Oadm.Sdk.Certificates.DeviceCertificatePurposes.Dot1x;
    public const string Mqtt = Oadm.Sdk.Certificates.DeviceCertificatePurposes.Mqtt;
    public const string Rtsps = Oadm.Sdk.Certificates.DeviceCertificatePurposes.Rtsps;
}

/// <summary>Plugin setting <c>config</c>.</summary>
public sealed record PkiConfig
{
    public int DeviceCertValidityDays { get; init; } = 365;

    public int ExpiryWarningDays { get; init; } = 30;

    public Dot1xConfig Dot1x { get; init; } = new();
}

/// <summary>IEEE 802.1X settings used by the 802.1X tasks.</summary>
public sealed record Dot1xConfig
{
    public int EapolVersion { get; init; } = 1;

    /// <summary><see cref="Dot1xIdentity"/>.</summary>
    public string Identity { get; init; } = Dot1xIdentity.Mac;

    /// <summary>Custom identity with the placeholders {serial} and {hostName}.</summary>
    public string CustomIdentity { get; init; } = string.Empty;

    /// <summary><see cref="RadiusCaSource"/>.</summary>
    public string RadiusCa { get; init; } = RadiusCaSource.Oadm;

    /// <summary>The imported RADIUS server CA (PEM, public only).</summary>
    public string? RadiusCaPem { get; init; }
}

/// <summary>Plugin setting <c>ca</c>: the active CA. The key is encrypted with the server master key.</summary>
public sealed record StoredCa
{
    /// <summary>SHA-256 fingerprint of the CA certificate (upper hex).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary><see cref="CaSource"/>.</summary>
    public string Source { get; init; } = CaSource.Generated;

    public string CertificatePem { get; init; } = string.Empty;

    /// <summary>Issuers above the CA (imported intermediate), leaf-issuer upwards; may be empty.</summary>
    public IReadOnlyList<string> ChainPem { get; init; } = [];

    /// <summary><c>Secrets.Protect(PKCS#8 PEM, "pki:ca:&lt;id&gt;")</c>.</summary>
    public string KeyProtected { get; init; } = string.Empty;

    public DateTime CreatedUtc { get; init; }
}

/// <summary>Plugin setting <c>previousCas</c> (newest first): public parts of replaced CAs.</summary>
public sealed record StoredPreviousCa
{
    public string Id { get; init; } = string.Empty;

    public string CertificatePem { get; init; } = string.Empty;

    public IReadOnlyList<string> ChainPem { get; init; } = [];

    public DateTime ReplacedUtc { get; init; }
}

/// <summary>
/// Plugin setting <c>importedCas</c>: CAs an administrator imported, oldest first: the certificate only, or with its key
/// (decision 2026-10-25: every import adds a CA; one with its key can issue).
/// </summary>
public sealed record StoredImportedCa
{
    /// <summary>SHA-256 of the certificate (upper hex); the authority id is <c>imported:&lt;Id&gt;</c>.</summary>
    public string Id { get; init; } = string.Empty;

    public string CertificatePem { get; init; } = string.Empty;

    /// <summary>Issuers above an imported intermediate CA (kept at import, nearest first); empty for a root or the certificate only.</summary>
    public IReadOnlyList<string> ChainPem { get; init; } = [];

    /// <summary>
    /// <c>Secrets.Protect(PKCS#8 PEM, "pki:ca:&lt;id&gt;")</c>, encrypted with the master key exactly like the OADM CA's key; null
    /// for a CA imported without its key. Never logged, never exported on its own.
    /// </summary>
    public string? KeyProtected { get; init; }

    public DateTime ImportedUtc { get; init; }

    /// <summary>
    /// Made on this server with Create CA (decision 2026-10-26), not imported: its key never left OADM, so like the first-start
    /// root CA it may be an EST sign-in CA.
    /// </summary>
    public bool Created { get; init; }
}

/// <summary>One row of the Certificate authorities card (<see cref="Oadm.Sdk.Certificates.CertificateAuthorityInfo"/> plus what the page shows).</summary>
public sealed record AuthorityRow
{
    /// <summary>The stable id ("oadm", "oadm-previous:&lt;sha256&gt;", "axis-device-id", "imported:&lt;sha256&gt;").</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Oadm.Sdk.Certificates.CertificateAuthorityKind Kind { get; init; }

    public DateTime NotAfterUtc { get; init; }

    /// <summary>SHA-256 of the first certificate, with colons.</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>The services that use it ("802.1X server"), in name order.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    /// <summary>The certificates (PEM, public), the CA's own first: View shows them.</summary>
    public IReadOnlyList<string> CertificatesPem { get; init; } = [];

    /// <summary>PKI holds the CA's key: it issues and can sign an intermediate CA (Create CA's Signed by).</summary>
    public bool Issues { get; init; }
}

public sealed record ImportAuthorityRequest(string? FileBase64, string? FileName);

/// <summary>
/// One device certificate the OADM CA issued: a row of the plugin records <c>issued</c> (by serial number; its DER in
/// <c>issuedDer</c>), written by <see cref="Ca.IssuedRegistry"/> only. Before 4.2 the plugin setting <c>issued</c> held them.
/// </summary>
public sealed record IssuedCertificate
{
    /// <summary>Upper-case hex as <c>X509Certificate2.SerialNumber</c> gives it (compared with <c>CertificateSerials.Normalize</c>).</summary>
    public string SerialNumber { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    /// <summary>The device's serial from the certificate's <c>axis-&lt;serial&gt;.local</c> name; null for entries from before 4.2.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary><see cref="CertificatePurpose"/>: https, dot1x, mqtt, rtsps.</summary>
    public string Purpose { get; init; } = CertificatePurpose.Https;

    /// <summary>Start of validity; null for entries from before 4.2.</summary>
    public DateTime? NotBeforeUtc { get; init; }

    public string CaId { get; init; } = string.Empty;

    public DateTime NotAfterUtc { get; init; }

    public DateTime IssuedUtc { get; init; }

    /// <summary>Alias of the certificate on the device ("OADM HTTPS 20261007-120000").</summary>
    public string? Alias { get; init; }

    /// <summary>Which plugin asked for it through the OADM CA service ("EST"); null = PKI's own tasks.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// OADM keeps no copy (DER) of it: an entry moved from OADM 4.1. The next read of the device's certificate list that finds
    /// it stores the copy and clears this.
    /// </summary>
    public bool NoCopy { get; init; }

    /// <summary>When a PKI task deleted it from the device; null while it is there. No longer listed (a copy could be in use).</summary>
    public DateTime? RemovedFromDeviceUtc { get; init; }

    /// <summary>
    /// Issued, not yet confirmed on the camera (docs/spec/pki.md "Current and pending"): a task before its verify step, EST
    /// before its answer was written (IEEE 802.1X: before the camera signed in with it). At most one per camera and use.
    /// </summary>
    public bool Pending { get; init; }

    /// <summary>
    /// A pending certificate the camera may use already: EST wrote its answer, or a task ran "Set 802.1X configuration" with
    /// it. A pending one a task delivered is never put on the revocation list as unused (device safety, no lock-out).
    /// </summary>
    public bool Delivered { get; init; }

    /// <summary>
    /// Not the camera's current certificate any more although it stays in the store: an entry from before 4.5 whose current
    /// one left the store (revoked or replaced), or a delivered pending one a newer pending one pushed out. Never listed.
    /// </summary>
    public bool NotCurrent { get; init; }
}

/// <summary>The active CA as the page shows it.</summary>
public sealed record CaInfo
{
    public string Id { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string CommonName { get; init; } = string.Empty;

    public string? Organization { get; init; }

    /// <summary><see cref="CaSource"/>.</summary>
    public string Source { get; init; } = CaSource.Generated;

    /// <summary>"RSA 4096", "ECC P-256".</summary>
    public string KeyType { get; init; } = string.Empty;

    public DateTime NotBeforeUtc { get; init; }

    public DateTime NotAfterUtc { get; init; }

    /// <summary>SHA-256 with colons ("3F:A2:...").</summary>
    public string Fingerprint { get; init; } = string.Empty;

    public bool IsIntermediate { get; init; }

    /// <summary>Common names of the issuers above the CA, nearest first.</summary>
    public IReadOnlyList<string> ChainSubjects { get; init; } = [];

    /// <summary>The CA certificate (PEM, public): the page installs it on the client computer.</summary>
    public string CertificatePem { get; init; } = string.Empty;
}

/// <summary>A replaced CA (public part only).</summary>
public sealed record PreviousCaInfo
{
    public string Id { get; init; } = string.Empty;

    public string CommonName { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public DateTime NotAfterUtc { get; init; }

    public DateTime ReplacedUtc { get; init; }

    /// <summary>Devices whose newest certificate came from this CA.</summary>
    public int Devices { get; init; }
}

/// <summary>Subject and validity of a certificate (the imported RADIUS server CA).</summary>
public sealed record CertificateSummary
{
    public string Subject { get; init; } = string.Empty;

    public string CommonName { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public DateTime NotBeforeUtc { get; init; }

    public DateTime NotAfterUtc { get; init; }

    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>Everything the page shows.</summary>
public sealed record PkiState
{
    public ServiceStatus Status { get; init; } = new(ServiceStatus.Neutral, "Loading");

    /// <summary>The active CA; null when there is none (yet).</summary>
    public CaInfo? Ca { get; init; }

    /// <summary>The stored CA key cannot be decrypted (other master key): only Generate / Import.</summary>
    public bool KeyUnreadable { get; init; }

    /// <summary>The stored first-start CA cannot be read (damaged): the CA list has a row that says so, with Delete.</summary>
    public bool CaUnreadable { get; init; }

    /// <summary>A CA is being created (RSA 4096 takes a few seconds).</summary>
    public bool IsGenerating { get; init; }

    /// <summary>The server offers no secret protection: the plugin cannot keep a CA.</summary>
    public bool Unavailable { get; init; }

    public IReadOnlyList<PreviousCaInfo> PreviousCas { get; init; } = [];

    public PkiConfig Config { get; init; } = new();

    /// <summary>The active CA is in the server's machine root store; null when unknown.</summary>
    public bool? ServerTrustInstalled { get; init; }

    /// <summary>Why the server store could not be checked or written.</summary>
    public string? ServerTrustError { get; init; }

    /// <summary>Devices with a certificate OADM issued that is still in use (not ended, revoked or deleted from the device).</summary>
    public int IssuedDevices { get; init; }

    public int DevicesWithCurrentCa { get; init; }

    public int DevicesWithPreviousCa { get; init; }

    /// <summary>Devices whose certificate in use ends within <see cref="PkiConfig.ExpiryWarningDays"/> (ended ones are not counted).</summary>
    public int ExpiringSoon { get; init; }

    /// <summary>The imported RADIUS server CA of the stored configuration.</summary>
    public CertificateSummary? RadiusCa { get; init; }

    /// <summary>Every CA PKI holds: the OADM CA, previous ones, the Axis device ID, imported CAs.</summary>
    public IReadOnlyList<AuthorityRow> Authorities { get; init; } = [];

    /// <summary>Every root CA of the list with its state in the server's root store (decision 2026-10-26: every root CA can be installed).</summary>
    public IReadOnlyList<RootTrustState> RootTrust { get; init; } = [];

    /// <summary>Set by the client's fake backend: nothing is installed on this computer.</summary>
    public bool Simulated { get; init; }
}

/// <summary>Create CA (decision 2026-10-26): a root CA (self-signed) or an intermediate CA signed by a listed CA with its key.</summary>
/// <param name="CommonName">The CA's name.</param>
/// <param name="Intermediate">True: signed by <paramref name="IssuerId"/>; false: a root CA.</param>
/// <param name="IssuerId">The authority id of the signing CA (<see cref="Oadm.Sdk.Certificates.CertificateAuthorityInfo.Id"/>); only for an intermediate.</param>
/// <param name="Key"><see cref="CaKeys"/>.</param>
/// <param name="ValidityYears">1 to 30; an intermediate ends at the latest with its issuer.</param>
public sealed record CreateCaRequest(string? CommonName, bool Intermediate, string? IssuerId, string? Key, int ValidityYears);

/// <summary>The key of a CA made on this server (Create CA).</summary>
public static class CaKeys
{
    public const string Ecc = "ecc";
    public const string Rsa2048 = "rsa2048";
    public const string Rsa3072 = "rsa3072";
    public const string Rsa4096 = "rsa4096";

    /// <summary>The default: RSA 4096, like the CA every OADM made before (cameras of every AXIS OS take it).</summary>
    public const string Default = Rsa4096;

    /// <summary>Every key in the choice's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Ecc, Rsa2048, Rsa3072, Rsa4096];

    /// <summary>"ECC P-256", "RSA 2048", ...</summary>
    public static string Name(string? key) => key switch
    {
        Ecc => "ECC P-256",
        Rsa2048 => "RSA 2048",
        Rsa3072 => "RSA 3072",
        _ => "RSA 4096",
    };

    /// <summary>The RSA key size of <paramref name="key"/>; null for ECC.</summary>
    public static int? RsaSize(string key) => key switch
    {
        Rsa2048 => 2048,
        Rsa3072 => 3072,
        Rsa4096 => 4096,
        _ => null,
    };
}

/// <summary>The Key column of a CA row that cannot issue (decision 2026-10-26).</summary>
public static class CaKeyTexts
{
    /// <summary>A CA certificate without its key (imported, Axis device ID, a previous CA).</summary>
    public const string None = "No key";

    /// <summary>A CA stored with a key the master key cannot read.</summary>
    public const string Unreadable = "Key cannot be read";
}

/// <param name="Id">The row id of the CA (<c>ca:&lt;SHA-256&gt;</c>, or the row of a CA that cannot be read).</param>
public sealed record DeleteAuthorityRequest(string? Id);

/// <param name="Fingerprint">SHA-256 (hex) of the root CA certificate; null: the first-start root CA.</param>
public sealed record InstallTrustRequest(string? Fingerprint);

/// <summary>A root CA of the list and whether the server's machine root store holds it (the tinted badges of its row).</summary>
public sealed record RootTrustState
{
    /// <summary>SHA-256 of the certificate (upper hex).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The certificate (PEM, public): the page checks and installs it on the client computer.</summary>
    public string CertificatePem { get; init; } = string.Empty;

    /// <summary>In the server's root store; null when unknown.</summary>
    public bool? ServerInstalled { get; init; }

    /// <summary>Why the server's store could not be checked or written.</summary>
    public string? ServerError { get; init; }
}

/// <param name="FileBase64">PKCS#12, or PEM certificate(s) (may contain the key), or a DER certificate.</param>
/// <param name="FileName">For the format guess and messages.</param>
/// <param name="Password">PKCS#12 password or the password of an encrypted key in the PEM file.</param>
/// <param name="KeyFileBase64">Separate PEM private key for a PEM certificate.</param>
/// <param name="KeyPassword">Password of an encrypted separate key.</param>
public sealed record ImportRequest(string? FileBase64, string? FileName, string? Password, string? KeyFileBase64, string? KeyPassword);

/// <param name="Format">"pem" (.crt) or "der" (.cer).</param>
public sealed record ExportRequest(string? Format);

public sealed record ExportPreviousRequest(string? Id, string? Format);

/// <param name="Password">The file's password (never stored or logged).</param>
/// <param name="Id">The authority id of a CA with its key; null: the first-start root CA.</param>
public sealed record BackupRequest(string? Password, string? Id = null);

/// <summary>Reply of the changing methods.</summary>
public sealed record PkiReply
{
    public bool Ok { get; init; }

    /// <summary>A user-level error without a field.</summary>
    public string? Error { get; init; }

    /// <summary>Field errors (field name -> message), shown under the fields.</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }

    public PkiState? State { get; init; }
}

/// <summary>A file for the save dialog.</summary>
public sealed record FileReply
{
    public string? FileName { get; init; }

    public string? DataBase64 { get; init; }

    public string? Error { get; init; }

    public IReadOnlyDictionary<string, string>? Errors { get; init; }
}

public sealed record InstallReply(bool Installed, string? Error, PkiState? State);

/// <summary>Field names of <see cref="PkiReply.Errors"/> (the page's property names).</summary>
public static class PkiFields
{
    public const string CommonName = "CommonName";
    public const string Organization = "Organization";
    public const string ValidityYears = "ValidityYears";
    public const string File = "File";
    public const string Password = "Password";
    public const string KeyFile = "KeyFile";
    public const string KeyPassword = "KeyPassword";
    public const string ConfirmPassword = "ConfirmPassword";
    public const string DeviceCertValidityDays = "DeviceCertValidityDays";
    public const string ExpiryWarningDays = "ExpiryWarningDays";
    public const string EapolVersion = "EapolVersion";
    public const string Identity = "Identity";
    public const string CustomIdentity = "CustomIdentity";
    public const string RadiusCa = "RadiusCa";
    public const string AuthorityFile = "AuthorityFile";

    /// <summary>Create CA: the CA that signs an intermediate.</summary>
    public const string Issuer = "Issuer";

    /// <summary>Create CA: the key.</summary>
    public const string Key = "Key";
}

public static class PkiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Throws <see cref="ArgumentException"/> (INVALID_ARGUMENT) for missing or unreadable JSON.</summary>
    public static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The request is empty.", nameof(json));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("The request is empty.", nameof(json));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request cannot be read: " + ex.Message, nameof(json), ex);
        }
    }

    /// <summary>Like <see cref="Deserialize{T}"/> but null for empty or unreadable JSON (stored settings).</summary>
    public static T? TryDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
