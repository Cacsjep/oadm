using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Contracts.Security;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Server.Hosting;

namespace Oadm.Server.Auth;

/// <summary>
/// The server's own TLS certificate for the gRPC endpoint: self-signed ECDSA P-256, CN = server name, SAN = host name,
/// machine name, localhost and every interface address, valid 20 years. Kept in <c>&lt;datafolder&gt;/server-tls.json</c>
/// (certificate PEM + PKCS#8 private key encrypted with the master key). Created on first start, or again with
/// <c>--Oadm:RegenerateTlsCertificate=true</c> or when the stored key cannot be decrypted (other master key). Clients pin
/// its SHA-256 fingerprint on first connect; the server logs it at startup so the user can compare.
/// <para>
/// Windows: SChannel needs the private key in a key container. The key is one persisted, named CNG key
/// (<see cref="KeyName"/>, Microsoft Software Key Storage Provider; machine key store when the server runs as a service,
/// else the user's), created once with the certificate, opened by name on every later start and overwritten when the
/// certificate is regenerated. The JSON file then holds the key name instead of the encrypted key, so no start leaves
/// another key file behind. Linux and macOS keep the encrypted PKCS#8 key in the file and load it in memory.
/// </para>
/// </summary>
public sealed partial class ServerTlsCertificate(OadmPaths paths, CredentialProtector protector, ServerSettingsStore settings, TimeProvider time, ILogger<ServerTlsCertificate> logger) : IDisposable
{
    public const string FileName = "server-tls.json";
    public const string RegenerateConfigKey = "Oadm:RegenerateTlsCertificate";
    private const string KeyPurpose = "oadm:server-tls";

    private X509Certificate2? _certificate;

    public string FilePath => Path.Combine(paths.DataDirectory, FileName);

    /// <summary>
    /// Windows: name of the persisted CNG key, "OADM Server TLS &lt;first 16 hex digits of the SHA-256 of the data folder&gt;"
    /// (one key per server data folder).
    /// </summary>
    public string KeyName =>
        "OADM Server TLS " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectory.ToUpperInvariant())))[..16];

    /// <summary>Windows: the key lives in the machine key store (installed service, SYSTEM) instead of the user's.</summary>
    public bool UseMachineKeyStore { get; init; } = ServiceHosting.IsRunningAsService();

    /// <summary>The certificate with its private key (after <see cref="LoadOrCreateAsync"/>).</summary>
    public X509Certificate2 Certificate => _certificate ?? throw new InvalidOperationException("The TLS certificate is not loaded yet.");

    /// <summary>SHA-256 fingerprint as clients show it ("3F:A2:...").</summary>
    public string Fingerprint => ServerCertificatePinning.Fingerprint(Certificate);

    public async Task<X509Certificate2> LoadOrCreateAsync(bool regenerate, CancellationToken ct)
    {
        if (_certificate is not null && !regenerate)
        {
            return _certificate;
        }

        X509Certificate2? loaded = null;
        if (!regenerate && File.Exists(FilePath))
        {
            loaded = TryLoad();
        }

        if (loaded is null)
        {
            var serverName = (await settings.GetServerSettingsAsync(ct).ConfigureAwait(false)).ServerName;
            loaded = Create(serverName);
            LogCreated(FilePath, regenerate ? "on request" : File.Exists(FilePath) ? "the stored one could not be read" : "first start");
        }

        _certificate?.Dispose();
        _certificate = loaded;
        var notAfter = loaded.NotAfter.ToUniversalTime();
        LogFingerprint(Fingerprint, notAfter);
        return loaded;
    }

    public void Dispose() => _certificate?.Dispose();

    private X509Certificate2? TryLoad()
    {
        try
        {
            var stored = JsonSerializer.Deserialize(File.ReadAllText(FilePath), TlsFileJsonContext.Default.TlsFile);
            if (stored is null || string.IsNullOrEmpty(stored.CertificatePem))
            {
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                // Only the named key; a file of an older build (encrypted key) gets a new certificate with a named key.
                return string.Equals(stored.KeyName, KeyName, StringComparison.Ordinal) ? OpenWithNamedKey(stored.CertificatePem) : null;
            }

            if (string.IsNullOrEmpty(stored.EncryptedKey))
            {
                return null;
            }

            var keyPem = protector.Unprotect(Convert.FromBase64String(stored.EncryptedKey), Encoding.UTF8.GetBytes(KeyPurpose));
            using var cert = X509Certificate2.CreateFromPem(stored.CertificatePem, keyPem);
            return Usable(cert);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or ArgumentException)
        {
            LogLoadFailed(ex, FilePath);
            return null;
        }
    }

    /// <summary>Windows: the stored certificate with the persisted named key (null when the key is gone).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private X509Certificate2? OpenWithNamedKey(string certificatePem)
    {
        if (!CngKey.Exists(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyOpenOptions))
        {
            return null;
        }

        using var publicOnly = X509Certificate2.CreateFromPem(certificatePem);
        using var cngKey = CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyOpenOptions);
        using var key = new ECDsaCng(cngKey);
        return publicOnly.CopyWithPrivateKey(key); // throws for a key that does not match: a new certificate is made
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private CngKeyOpenOptions KeyOpenOptions => UseMachineKeyStore ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

    /// <summary>Windows: creates (or overwrites) the persisted named key; not exportable.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private ECDsaCng CreateNamedKey()
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey | (UseMachineKeyStore ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None),
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        using var cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, parameters);
        return new ECDsaCng(cngKey);
    }

    private X509Certificate2 Create(string serverName)
    {
        using ECDsa key = OperatingSystem.IsWindows() ? CreateNamedKey() : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName.Trim();
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        subject.AddOrganizationName("OADM");
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in DnsNames(name))
        {
            san.AddDnsName(dns);
        }

        foreach (var ip in Addresses())
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = time.GetUtcNow();
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(20));
        var file = new TlsFile
        {
            CertificatePem = cert.ExportCertificatePem(),
            EncryptedKey = OperatingSystem.IsWindows()
                ? string.Empty
                : Convert.ToBase64String(protector.Protect(key.ExportPkcs8PrivateKeyPem(), Encoding.UTF8.GetBytes(KeyPurpose))),
            KeyName = OperatingSystem.IsWindows() ? KeyName : null,
            CreatedUtc = now.UtcDateTime,
        };
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, TlsFileJsonContext.Default.TlsFile));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Windows: the certificate is already bound to the persisted named key, which SChannel accepts as it is.
        return OperatingSystem.IsWindows() ? new X509Certificate2(cert) : Usable(cert);
    }

    /// <summary>
    /// Linux and macOS: a copy SslStream can use, through a PKCS#12 round trip of the in-memory key (never on Windows,
    /// where an imported PKCS#12 key would be written to the key store on every start).
    /// </summary>
    private static X509Certificate2 Usable(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static HashSet<string> DnsNames(string serverName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        foreach (var candidate in new[] { serverName, Environment.MachineName, SafeHostName() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Uri.CheckHostName(candidate) == UriHostNameType.Dns)
            {
                names.Add(candidate);
            }
        }

        return names;
    }

    private static string? SafeHostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static HashSet<IPAddress> Addresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 && unicast.Address.ScopeId != 0 ? new IPAddress(unicast.Address.GetAddressBytes()) : unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Loopback only.
        }

        return addresses;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the server TLS certificate {Path} ({Reason})")]
    private partial void LogCreated(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server TLS certificate fingerprint (SHA-256): {Fingerprint}, valid until {NotAfter:yyyy-MM-dd}. Clients show it on their first connection; compare before trusting it.")]
    private partial void LogFingerprint(string fingerprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server TLS certificate {Path} could not be read (for example after the master key was replaced); creating a new one. Clients show \"The server certificate changed\" and must use Forget server and confirm the new fingerprint")]
    private partial void LogLoadFailed(Exception ex, string path);
}

/// <summary>Content of server-tls.json.</summary>
internal sealed class TlsFile
{
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>Linux, macOS: PKCS#8 PEM of the private key, encrypted with the master key (AES-256-GCM), base64.</summary>
    public string EncryptedKey { get; set; } = string.Empty;

    /// <summary>Windows: name of the persisted CNG key (the key never leaves the key store).</summary>
    public string? KeyName { get; set; }

    public DateTime CreatedUtc { get; set; }
}

[JsonSerializable(typeof(TlsFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TlsFileJsonContext : JsonSerializerContext;
