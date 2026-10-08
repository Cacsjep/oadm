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

namespace Oadm.Server.Auth;

/// <summary>
/// The server's own TLS certificate for the gRPC endpoint: self-signed ECDSA P-256, CN = server name, SAN = host name,
/// machine name, localhost and every interface address, valid 20 years. Kept in <c>&lt;datafolder&gt;/server-tls.json</c>
/// (certificate PEM + PKCS#8 private key encrypted with the master key). Created on first start, or again with
/// <c>--Oadm:RegenerateTlsCertificate=true</c> or when the stored key cannot be decrypted (other master key). Clients pin
/// its SHA-256 fingerprint on first connect; the server logs it at startup so the user can compare.
/// </summary>
public sealed partial class ServerTlsCertificate(OadmPaths paths, CredentialProtector protector, ServerSettingsStore settings, TimeProvider time, ILogger<ServerTlsCertificate> logger) : IDisposable
{
    public const string FileName = "server-tls.json";
    public const string RegenerateConfigKey = "Oadm:RegenerateTlsCertificate";
    private const string KeyPurpose = "oadm:server-tls";

    private X509Certificate2? _certificate;

    public string FilePath => Path.Combine(paths.DataDirectory, FileName);

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
            if (stored is null || string.IsNullOrEmpty(stored.CertificatePem) || string.IsNullOrEmpty(stored.EncryptedKey))
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

    private X509Certificate2 Create(string serverName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
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
            EncryptedKey = Convert.ToBase64String(protector.Protect(key.ExportPkcs8PrivateKeyPem(), Encoding.UTF8.GetBytes(KeyPurpose))),
            CreatedUtc = now.UtcDateTime,
        };
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, TlsFileJsonContext.Default.TlsFile));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return Usable(cert);
    }

    /// <summary>
    /// A copy SslStream can use on every OS: Windows SChannel needs the key in a key container, so the certificate goes
    /// through a PKCS#12 round trip (an in-memory ECDsa key is refused there).
    /// </summary>
    private static X509Certificate2 Usable(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
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

    /// <summary>PKCS#8 PEM of the private key, encrypted with the master key (AES-256-GCM), base64.</summary>
    public string EncryptedKey { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

[JsonSerializable(typeof(TlsFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TlsFileJsonContext : JsonSerializerContext;
