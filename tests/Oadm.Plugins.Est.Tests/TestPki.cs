using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Tests;

/// <summary>Test certificates: a device ID hierarchy like Axis' (root, intermediate, device) and a fake OADM CA.</summary>
internal static class TestPki
{
    public static X509Certificate2 Root(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + Unique(name), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(30));
    }

    public static X509Certificate2 Intermediate(X509Certificate2 issuer, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + Unique(name), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddYears(20), Serial());
        return signed.CopyWithPrivateKey(key);
    }

    /// <summary>A device ID certificate with the camera's serial in the subject (X.520 serialNumber), with its key.</summary>
    public static X509Certificate2 DeviceId(X509Certificate2 issuer, string serial, int days = 3650)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationName("Axis Communications AB");
        name.Add("2.5.4.5", serial);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days), Serial());
        using var withKey = signed.CopyWithPrivateKey(key);
        return Usable(withKey);
    }

    /// <summary>Through PKCS#12, so TLS can use the key on every OS.</summary>
    public static X509Certificate2 Usable(X509Certificate2 withKey) =>
        X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

    /// <summary>A PKCS#10 request (DER) and its key.</summary>
    public static (byte[] Csr, ECDsa Key) Request(string subject = "CN=camera")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSigningRequest(), key);
    }

    /// <summary>A name of its own per test CA: Windows caches chains by name, CAs of other tests with the same name mix up.</summary>
    private static string Unique(string name) => name + " " + Guid.NewGuid().ToString("N")[..8];

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        return serial;
    }
}

/// <summary>The OADM CA as the PKI plugin offers it, for tests: signs with OADM's names, records every request.</summary>
internal sealed class FakeAuthority : IDeviceCertificateAuthority, Oadm.Plugins.Pki.Ca.ICertificateDelivery, Oadm.Plugins.Pki.Ca.ICurrentCertificates,
    Oadm.Plugins.Pki.Ca.IServerCertificateSource, Oadm.Plugins.Pki.Ca.IRadiusCaSource, IDisposable
{
    private readonly Lock _serverGate = new();
    private Oadm.Plugins.Pki.ServerCertificateMaterial? _server;

    /// <summary>The issuer chain of the servers' certificate when it is not this CA (a previous or an imported CA); null: this CA.</summary>
    public IReadOnlyList<byte[]>? ServerIssuer { get; set; }

    /// <summary>The servers' certificate names <paramref name="name"/> too (made again at the next read).</summary>
    public void NameAlso(string name)
    {
        lock (_serverGate)
        {
            ServerNames.Add(name);
            _server = null;
        }
    }

    /// <summary>The RADIUS server CA of an IEEE 802.1X policy; null: this CA. Throw to refuse like PKI.</summary>
    public Func<Oadm.Plugins.Pki.Policies.PkiPolicy, IReadOnlyList<(string Alias, string Pem, string Fingerprint)>>? RadiusCaAnswer { get; set; }

    public IReadOnlyList<(string Alias, string Pem, string Fingerprint)> RadiusCas(Oadm.Plugins.Pki.Policies.PkiPolicy policy) =>
        RadiusCaAnswer?.Invoke(policy) ?? [("OADM CA test", new string(PemEncoding.Write("CERTIFICATE", _ca.RawData)) + "\n", Convert.ToHexString(SHA256.HashData(_ca.RawData)))];

    /// <summary>How often the servers' certificate was made (in PKI the server policy picks it from the list, decision 2026-10-25).</summary>
    public int ServerCertificatesMade { get; private set; }

    /// <summary>The names of the servers' certificate (the addresses the tests connect to, and a site's node address).</summary>
    public List<string> ServerNames { get; } = ["127.0.0.1", "10.0.0.17", "localhost", "oadm-server"];

    /// <summary>The certificate the EST server shows: one of this CA, made on the first read; none while the CA is not ready.</summary>
    public Oadm.Plugins.Pki.ServerCertificateLoad ServerCertificate(string kind)
    {
        if (!IsReady)
        {
            return new(null, null);
        }

        lock (_serverGate)
        {
            _server ??= MakeServerCertificate(); // the server's start and the first connection may read it at once
            using var leaf = X509CertificateLoader.LoadCertificate(_server.Leaf);
            if (IsRevoked(leaf.SerialNumber))
            {
                return new(null, "The EST server cannot use its certificate: it is revoked."); // as PKI says it
            }

            return new(_server with { Pkcs8 = (byte[])_server.Pkcs8.Clone(), IssuerChain = ServerIssuerChain(kind) }, null); // the reader clears its copy of the key
        }
    }

    /// <summary>The serial of the servers' certificate now (made on the first read).</summary>
    public string ServerSerial()
    {
        lock (_serverGate)
        {
            _server ??= MakeServerCertificate();
            using var leaf = X509CertificateLoader.LoadCertificate(_server.Leaf);
            return leaf.SerialNumber;
        }
    }

    public IReadOnlyList<byte[]> ServerIssuerChain(string kind) => IsReady ? ServerIssuer ?? Chain : [];

    public Task EnsureServerCertificateAsync(string kind, IReadOnlyList<string> names, Func<CancellationToken, Task<Oadm.Plugins.Pki.ServerCertificateImport?>>? existing, CancellationToken ct) =>
        Task.CompletedTask;

    private Oadm.Plugins.Pki.ServerCertificateMaterial MakeServerCertificate()
    {
        ServerCertificatesMade++;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=802.1X and EST server", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in ServerNames)
        {
            if (System.Net.IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        using var caKey = _ca.GetECDsaPrivateKey()!;
        using var signed = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10),
            [.. Guid.NewGuid().ToByteArray().Take(16)]);
        return new Oadm.Plugins.Pki.ServerCertificateMaterial("test-server", signed.RawData, Chain, key.ExportPkcs8PrivateKey(), signed.NotAfter.ToUniversalTime(), [.. ServerNames]);
    }

    /// <summary>The cameras' current certificates as PKI's store holds them: DER (hex) -> camera serial.</summary>
    public Dictionary<string, string> Current { get; } = new(StringComparer.Ordinal);

    public Task<string?> CurrentCameraAsync(byte[] certificateDer, CancellationToken ct) =>
        Task.FromResult(Current.GetValueOrDefault(Convert.ToHexString(certificateDer)));

    private readonly X509Certificate2 _ca = TestPki.Root("OADM Test CA");

    /// <summary>What EST told the store: ("delivered" | "not delivered" | "signed in", serial), in order.</summary>
    public List<(string What, string Serial)> Deliveries { get; } = [];

    /// <summary>PKI's store fails when EST tells it a certificate was delivered.</summary>
    public bool FailDelivered { get; set; }

    public Task DeliveredAsync(string serialNumber, CancellationToken ct) =>
        FailDelivered ? throw new InvalidOperationException("PKI's store cannot be written.") : Note("delivered", serialNumber);

    public Task NotDeliveredAsync(string serialNumber, CancellationToken ct) => Note("not delivered", serialNumber);

    public Task SignedInAsync(string serialNumber, CancellationToken ct) => Note("signed in", serialNumber);

    private Task Note(string what, string serial)
    {
        lock (Deliveries)
        {
            Deliveries.Add((what, CertificateSerials.Normalize(serial)));
        }

        return Task.CompletedTask;
    }

    public bool IsReady { get; set; } = true;

    public IReadOnlyList<byte[]> Chain => [_ca.RawData];

    public X509Certificate2 Certificate => _ca;

    public List<DeviceCertificateRequest> Requests { get; } = [];

    public event EventHandler? Changed;

    public Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(request.Csr.ToArray(), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateRequestRefusedException("The certificate request cannot be read.", ex);
        }

        var signing = new CertificateRequest(new X500DistinguishedName("CN=" + request.CommonName), csr.PublicKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ip in request.IpAddresses)
        {
            san.AddIpAddress(System.Net.IPAddress.Parse(ip));
        }

        foreach (var dns in request.DnsNames)
        {
            san.AddDnsName(dns);
        }

        signing.CertificateExtensions.Add(san.Build());
        signing.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signing.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(request.Use == DeviceCertificateUse.Client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        signing.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));
        var serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        var notAfter = DateTimeOffset.UtcNow + (request.Validity ?? TimeSpan.FromDays(365));
        using var caKey = _ca.GetECDsaPrivateKey()!; // the generator: the request may hold an RSA key
        using var signed = signing.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, serial);
        return Task.FromResult(new IssuedDeviceCertificate(signed.RawData, Chain, signed.SerialNumber.ToUpperInvariant(), signed.NotAfter.ToUniversalTime()));
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>PKI's list of CAs (SDK 0.4.0); empty = a PKI without the list.</summary>
    public List<CertificateAuthorityInfo> AuthorityList { get; } = [];

    public IReadOnlyList<CertificateAuthorityInfo> Authorities => AuthorityList;

    /// <summary>What the services told with SetUsedBy, by service id.</summary>
    public Dictionary<string, (string Name, IReadOnlyCollection<string> Ids)> UsedBy { get; } = [];

    public void SetUsedBy(string serviceId, string serviceName, IReadOnlyCollection<string> authorityIds)
    {
        lock (UsedBy)
        {
            if (authorityIds.Count == 0)
            {
                UsedBy.Remove(serviceId);
            }
            else
            {
                UsedBy[serviceId] = (serviceName, [.. authorityIds]);
            }
        }
    }

    /// <summary>The OADM CA and the Axis device ID as PKI lists them.</summary>
    public void ListDefaults() => AuthorityList.AddRange(
    [
        new(CertificateAuthorityInfo.OadmId, "OADM Test CA", CertificateAuthorityKind.Oadm, Chain, _ca.NotAfter.ToUniversalTime(), true),
        new(CertificateAuthorityInfo.AxisDeviceIdId, "Axis device ID", CertificateAuthorityKind.AxisDeviceId, [], DateTime.UtcNow.AddYears(10), false),
    ]);

    /// <summary>The revocation list (PKI's, SDK 0.4.0), by normalized serial.</summary>
    public Dictionary<string, CertificateRevocation> Revoked { get; } = [];

    public void Revoke(string serialNumber, RevocationReason reason = RevocationReason.KeyCompromised) =>
        Revoked[CertificateSerials.Normalize(serialNumber)] = new CertificateRevocation(CertificateSerials.Normalize(serialNumber), new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), reason, "admin");

    public bool IsRevoked(string serialNumber) => Revoked.ContainsKey(CertificateSerials.Normalize(serialNumber));

    /// <summary>Serials PKI lists as created, signed or imported on its page (no device's certificate).</summary>
    public HashSet<string> NotDevice { get; } = new(StringComparer.Ordinal);

    public string? PurposeOf(string serialNumber) =>
        NotDevice.Contains(CertificateSerials.Normalize(serialNumber)) ? Oadm.Plugins.Pki.Ca.PkiCertificateAuthority.NotDevicePurpose : null;

    public CertificateRevocation? FindRevocation(string serialNumber) => Revoked.GetValueOrDefault(CertificateSerials.Normalize(serialNumber));

    public void Dispose() => _ca.Dispose();
}
