using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Identity;

/// <summary>
/// The node's TLS identity (docs/sites.md, "Identity"): ECDSA P-256, self-signed, 100 years (<see cref="SiteTls.CreateIdentity"/>),
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
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
            _certificate.Dispose();
    }

    private X509Certificate2? TryLoad()
    {
        if (!File.Exists(_identityPath) || !File.Exists(_keyPath))
            return null;
        try
        {
            var key = File.ReadAllBytes(_keyPath);
            var data = File.ReadAllBytes(_identityPath);
            if (key.Length != 32 || data.Length < 28)
                return null;
            var plain = new byte[data.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, "oadm-site-node-identity"u8);
            return X509CertificateLoader.LoadPkcs12(plain, null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception e) when (e is CryptographicException or IOException)
        {
            return null; // a new identity: the server sees an unknown node, the node must be bound again
        }
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

    private static void WritePrivate(string path, byte[] data)
    {
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temp, data);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(data);
        }

        File.Move(temp, path, overwrite: true);
    }
}
