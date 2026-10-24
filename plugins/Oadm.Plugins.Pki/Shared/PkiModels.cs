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

    /// <summary>Most serial numbers of one Revoke.</summary>
    public const int MaxRevokeAtOnce = 5000;

    /// <summary>"OADM Root CA &lt;server name&gt;".</summary>
    public static string DefaultCommonName(string serverName) => "OADM Root CA " + serverName;
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and the live event topic.</summary>
public static class PkiMethods
{
    /// <summary>() -> <see cref="PkiState"/>.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="GenerateRequest"/> -> <see cref="PkiReply"/>.</summary>
    public const string Generate = "generate";

    /// <summary><see cref="ImportRequest"/> -> <see cref="PkiReply"/>.</summary>
    public const string Import = "import";

    /// <summary>() -> <see cref="PreviewReplaceReply"/>.</summary>
    public const string PreviewReplace = "previewReplace";

    /// <summary><see cref="ExportRequest"/> -> <see cref="FileReply"/>.</summary>
    public const string ExportPublic = "exportPublic";

    /// <summary><see cref="ExportPreviousRequest"/> -> <see cref="FileReply"/>.</summary>
    public const string ExportPrevious = "exportPrevious";

    /// <summary><see cref="RemovePreviousRequest"/> -> <see cref="PkiReply"/>.</summary>
    public const string RemovePrevious = "removePrevious";

    /// <summary><see cref="BackupRequest"/> -> <see cref="FileReply"/> (PKCS#12 with the key).</summary>
    public const string Backup = "backup";

    /// <summary>() -> <see cref="InstallReply"/>: the CA into the server's machine root store.</summary>
    public const string InstallServerTrust = "installServerTrust";

    /// <summary><see cref="SaveSettingsRequest"/> -> <see cref="PkiReply"/>.</summary>
    public const string SaveSettings = "saveSettings";

    /// <summary><see cref="ImportRadiusCaRequest"/> -> <see cref="RadiusCaReply"/> (checked, not stored: Save stores it).</summary>
    public const string ImportRadiusCa = "importRadiusCa";

    /// <summary>Event: <see cref="PkiState"/> after every change.</summary>
    public const string StateTopic = "state";

    /// <summary><see cref="CertificatesRequest"/> -> <see cref="CertificatesReply"/>: one page of the issued certificates.</summary>
    public const string GetCertificates = "getCertificates";

    /// <summary><see cref="CertificateDetailsRequest"/> -> <see cref="CertificateDetailsReply"/>: one issued certificate in plain names.</summary>
    public const string GetCertificate = "getCertificate";

    /// <summary><see cref="RevokeRequest"/> -> <see cref="RevokeReply"/> (users with all devices, audited).</summary>
    public const string Revoke = "revoke";

    /// <summary><see cref="RevokeDeviceRequest"/> -> <see cref="RevokeReply"/>: every certificate of one device that is still valid.</summary>
    public const string RevokeDevice = "revokeDevice";

    /// <summary>Event (payload <c>{}</c>): the issued certificates or the revocation list changed; pages read their page again.</summary>
    public const string CertificatesTopic = "certificates";
}

/// <summary>The payload of <see cref="PkiMethods.GetCertificates"/>; filters as in <see cref="Oadm.Sdk.Certificates.IssuedCertificateQuery"/>.</summary>
public sealed record CertificatesRequest
{
    public Oadm.Sdk.Certificates.IssuedCertificateState? State { get; init; }

    public string? Purpose { get; init; }

    public string? Source { get; init; }

    public string? Search { get; init; }

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

    /// <summary>The days before the end that count as "Expires soon".</summary>
    public int WarningDays { get; init; } = 30;
}

/// <summary>The payload of <see cref="PkiMethods.GetCertificate"/>.</summary>
public sealed record CertificateDetailsRequest(string SerialNumber);

/// <summary>One issued certificate; <see cref="Error"/> when it is not listed (or not one of the user's devices).</summary>
public sealed record CertificateDetailsReply
{
    public Oadm.Sdk.Certificates.CertificateDescription? Certificate { get; init; }

    public Oadm.Sdk.Certificates.CertificateRevocation? Revocation { get; init; }

    public string? Error { get; init; }
}

/// <summary>The payload of <see cref="PkiMethods.Revoke"/>.</summary>
public sealed record RevokeRequest(IReadOnlyList<string> SerialNumbers, Oadm.Sdk.Certificates.RevocationReason Reason);

/// <summary>The payload of <see cref="PkiMethods.RevokeDevice"/>.</summary>
public sealed record RevokeDeviceRequest(Guid DeviceId, Oadm.Sdk.Certificates.RevocationReason Reason);

/// <summary>What a revoke did.</summary>
public sealed record RevokeReply
{
    public int Revoked { get; init; }

    public int AlreadyRevoked { get; init; }

    public int NotFound { get; init; }

    public string? Error { get; init; }
}

/// <summary>EAP identity choices of 802.1X.</summary>
public static class Dot1xIdentity
{
    public const string Mac = "mac";
    public const string HostName = "hostName";
    public const string Custom = "custom";
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

    /// <summary>When a PKI task deleted it from the device; null while it is there. Listed as Replaced (a copy could be in use).</summary>
    public DateTime? RemovedFromDeviceUtc { get; init; }
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

    /// <summary>"RSA 4096", "ECDSA P-256".</summary>
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

    /// <summary>Set by the client's fake backend: nothing is installed on this computer.</summary>
    public bool Simulated { get; init; }
}

public sealed record GenerateRequest(string? CommonName, string? Organization, int ValidityYears, bool Confirmed);

/// <param name="FileBase64">PKCS#12, or PEM certificate(s) (may contain the key), or a DER certificate.</param>
/// <param name="FileName">For the format guess and messages.</param>
/// <param name="Password">PKCS#12 password or the password of an encrypted key in the PEM file.</param>
/// <param name="KeyFileBase64">Separate PEM private key for a PEM certificate.</param>
/// <param name="KeyPassword">Password of an encrypted separate key.</param>
/// <param name="Confirmed">The user confirmed replacing the current CA.</param>
public sealed record ImportRequest(string? FileBase64, string? FileName, string? Password, string? KeyFileBase64, string? KeyPassword, bool Confirmed);

/// <param name="Format">"pem" (.crt) or "der" (.cer).</param>
public sealed record ExportRequest(string? Format);

public sealed record ExportPreviousRequest(string? Id, string? Format);

public sealed record RemovePreviousRequest(string? Id);

public sealed record BackupRequest(string? Password);

public sealed record SaveSettingsRequest(PkiConfig? Config);

public sealed record ImportRadiusCaRequest(string? FileBase64);

/// <summary>Reply of the changing methods.</summary>
public sealed record PkiReply
{
    public bool Ok { get; init; }

    /// <summary>A user-level error without a field.</summary>
    public string? Error { get; init; }

    /// <summary>Field errors (field name -> message), shown under the fields.</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }

    /// <summary>Replacing needs the user's confirmation; nothing was changed.</summary>
    public bool NeedsConfirmation { get; init; }

    public int DevicesWithCurrentCa { get; init; }

    public PkiState? State { get; init; }
}

public sealed record PreviewReplaceReply(int DevicesWithCurrentCa);

/// <summary>A file for the save dialog.</summary>
public sealed record FileReply
{
    public string? FileName { get; init; }

    public string? DataBase64 { get; init; }

    public string? Error { get; init; }

    public IReadOnlyDictionary<string, string>? Errors { get; init; }
}

public sealed record InstallReply(bool Installed, string? Error, PkiState? State);

public sealed record RadiusCaReply
{
    public string? Pem { get; init; }

    public CertificateSummary? Summary { get; init; }

    public IReadOnlyDictionary<string, string>? Errors { get; init; }
}

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
