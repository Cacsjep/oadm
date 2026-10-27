using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.SiteNode.Config;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Identity;

/// <summary>
/// The node's TLS identity (src/Oadm.SiteNode/README.md, "Data folder"): ECDSA P-256, self-signed, 100 years (<see cref="SiteTls.CreateIdentity"/>),
/// new at first start and at every Reset. Stored as a PKCS#12 encrypted with AES-256-GCM under a local key file
/// (local.key, 32 random bytes, readable by the service account only: 0600 on Linux, the data folder's ACL on Windows).
/// </summary>
public sealed class NodeIdentity : IDisposable
{
    public const string KeyFileName = "local.key";
    public const string IdentityFileName = "identity.bin";

    private readonly string _keyPath;
    private readonly string _identityPath;
    private readonly Lock _lock = new();
    private X509Certificate2 _certificate;

    public NodeIdentity(string dataDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _keyPath = Path.Combine(dataDirectory, KeyFileName);
        _identityPath = Path.Combine(dataDirectory, IdentityFileName);
        _certificate = TryLoad() ?? CreateAndSave(name);
    }

    /// <summary>Raised after <see cref="Renew"/>.</summary>
    public event Action? Changed;

    public X509Certificate2 Certificate
    {
        get
        {
            lock (_lock)
                return _certificate;
        }
    }

    /// <summary>SHA-256 of the certificate: what the server pins.</summary>
    public byte[] Fingerprint => SiteTls.Fingerprint(Certificate);

    public string FingerprintHex => Convert.ToHexString(Fingerprint);

    /// <summary>A new key and certificate (Reset). Links with the old one must be closed by the caller.</summary>
    public void Renew(string name)
    {
        var next = CreateAndSave(name);
        lock (_lock)
            _certificate = next;
        NodeEvents.Raise(Changed);
    }

    public void Dispose()
    {
        lock (_lock)
            _certificate.Dispose();
    }

    // Both files absent = first start. A file that cannot be read fails the start (DataFiles), so a passing disk problem
    // never replaces the identity the server pinned. Only an identity that reads but cannot be decrypted (the key does
    // not fit, damaged content) is replaced; the old files are kept as .unreadable.
    private X509Certificate2? TryLoad()
    {
        if (!DataFiles.Exists(_identityPath) && !DataFiles.Exists(_keyPath))
            return null;
        var key = DataFiles.Exists(_keyPath) ? DataFiles.ReadAllBytes(_keyPath) : [];
        var data = DataFiles.Exists(_identityPath) ? DataFiles.ReadAllBytes(_identityPath) : [];
        try
        {
            if (key.Length != 32 || data.Length < 28)
                throw new CryptographicException("The identity files are incomplete.");
            var plain = new byte[data.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, "oadm-site-node-identity"u8);
            return X509CertificateLoader.LoadPkcs12(plain, null, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            // A new identity: the server sees an unknown node, the node must be bound again.
            KeepAside(_identityPath);
            KeepAside(_keyPath);
            return null;
        }
    }

    private static void KeepAside(string path)
    {
        if (File.Exists(path))
            File.Move(path, path + ".unreadable", overwrite: true);
    }

    private X509Certificate2 CreateAndSave(string name)
    {
        var certificate = SiteTls.CreateIdentity(string.IsNullOrWhiteSpace(name) ? "OADM Site Node" : name);
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = certificate.Export(X509ContentType.Pkcs12);
        var data = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, 12));
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(data.AsSpan(0, 12), plain, data.AsSpan(28), data.AsSpan(12, 16), "oadm-site-node-identity"u8);
        WritePrivate(_keyPath, key);
        WritePrivate(_identityPath, data);
        return certificate;
    }

    private static void WritePrivate(string path, byte[] data) => DataFiles.WriteDurable(path, data, ownerOnly: true);
}
