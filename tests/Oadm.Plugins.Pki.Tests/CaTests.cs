using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Generating and importing CAs (no OS store involved).</summary>
public sealed class CaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Generate_creates_an_rsa_4096_root_with_the_specified_extensions()
    {
        using var ca = CaGenerator.Generate("OADM Root CA SERVER01", "Acme", 10, Now);
        var cert = ca.Certificate;

        Assert.Equal("CN=OADM Root CA SERVER01, O=Acme", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.Equal("RSA 4096", CaCertificates.KeyType(cert));
        Assert.Equal("1.2.840.113549.1.1.11", cert.SignatureAlgorithm.Value); // sha256WithRSAEncryption (PKCS#1 v1.5)
        Assert.Equal(16, cert.SerialNumberBytes.Length);
        Assert.True((cert.SerialNumberBytes.Span[0] & 0x80) == 0, "positive serial");
        Assert.InRange(cert.NotBefore.ToUniversalTime(), Now.UtcDateTime.AddMinutes(-5).AddSeconds(-2), Now.UtcDateTime.AddMinutes(-5).AddSeconds(2));
        Assert.Equal(Now.UtcDateTime.AddYears(10).Date, cert.NotAfter.ToUniversalTime().Date);

        var basic = Assert.Single(cert.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(basic.Critical);
        Assert.True(basic.CertificateAuthority);
        Assert.False(basic.HasPathLengthConstraint);
        var usage = Assert.Single(cert.Extensions.OfType<X509KeyUsageExtension>());
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);
        var ski = Assert.Single(cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>());
        var aki = Assert.Single(cert.Extensions.OfType<X509AuthorityKeyIdentifierExtension>());
        Assert.Equal(ski.SubjectKeyIdentifier, Convert.ToHexString(aki.KeyIdentifier!.Value.Span));

        Assert.True(ca.HasKey);
        Assert.Contains("-----BEGIN PRIVATE KEY-----", ca.KeyPkcs8Pem, StringComparison.Ordinal);
        Assert.Equal(CaCertificates.Fingerprint(cert), ca.Id);
        Assert.Matches("^[0-9A-F]{64}$", ca.Id);
        Assert.False(ca.Describe(CaSource.Generated).IsIntermediate);
    }

    [Fact]
    public void Generated_ca_signs_a_device_certificate_that_chains_to_it()
    {
        using var ca = CaGenerator.Generate("Test CA", null, 1, Now, keySize: 2048);
        using var signer = ca.WithPrivateKey();
        using var leaf = TestCa.Leaf(signer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(chain.Build(leaf));
        Assert.Equal("CN=Test CA", ca.Certificate.Subject);
    }

    [Fact]
    public void Import_pkcs12_with_chain_orders_the_issuers_upwards()
    {
        using var root = TestCa.Root();
        using var intermediate = TestCa.Intermediate(root);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = TestCa.Pfx("secret", rootPublic, intermediate);

        var result = CaImporter.Import(new CaImportInput(pfx, "acme.pfx", "secret", null, null), Now, 365);

        Assert.Empty(result.Errors);
        using var ca = result.Ca!;
        Assert.Equal(intermediate.Thumbprint, ca.Certificate.Thumbprint);
        Assert.False(ca.Certificate.HasPrivateKey);
        Assert.Equal(root.Thumbprint, Assert.Single(ca.Chain).Thumbprint);
        var info = ca.Describe(CaSource.Imported);
        Assert.True(info.IsIntermediate);
        Assert.Equal(["Acme Root CA"], info.ChainSubjects);
        Assert.Equal("Acme", info.Organization);
        Assert.Equal("RSA 2048", info.KeyType);
    }

    [Fact]
    public void Import_pkcs12_with_a_wrong_password_is_a_password_error()
    {
        using var root = TestCa.Root();
        var result = CaImporter.Import(new CaImportInput(TestCa.Pfx("secret", root), "a.pfx", "wrong", null, null), Now, 365);

        Assert.Null(result.Ca);
        Assert.Equal(CaImporter.WrongPassword, result.Errors[PkiFields.Password]);
    }

    [Fact]
    public void Import_pkcs12_without_key_or_with_two_keys_is_refused()
    {
        using var root = TestCa.Root();
        using var other = TestCa.Root("Other CA");
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);

        Assert.Equal("The file contains no private key.", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", rootPublic), null, "pw", null, null), Now, 365).Errors[PkiFields.File]);
        Assert.Equal("The file contains more than one certificate with a private key.", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", root, other), null, "pw", null, null), Now, 365).Errors[PkiFields.File]);
    }

    [Theory]
    [InlineData("pkcs8")]
    [InlineData("pkcs1")]
    [InlineData("together")]
    public void Import_pem_certificate_with_rsa_key(string keyFormat)
    {
        using var rsa = RSA.Create(2048);
        using var root = TestCa.Root(rsa: rsa);
        var keyPem = keyFormat == "pkcs1" ? rsa.ExportRSAPrivateKeyPem() : rsa.ExportPkcs8PrivateKeyPem();
        var input = keyFormat == "together"
            ? new CaImportInput(TestCa.Ascii(CaCertificates.ToPem(root) + keyPem), "ca.pem", null, null, null)
            : new CaImportInput(TestCa.Pem(root), "ca.crt", null, TestCa.Ascii(keyPem), null);

        var result = CaImporter.Import(input, Now, 365);

        Assert.Empty(result.Errors);
        using var ca = result.Ca!;
        Assert.Equal(root.Thumbprint, ca.Certificate.Thumbprint);
        Assert.Empty(ca.Chain);
        using var signer = ca.WithPrivateKey();
        Assert.True(signer.HasPrivateKey);
    }

    [Fact]
    public void Import_pem_with_an_ec_sec1_key_and_a_der_certificate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=EC Root", ec, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(10));

        var result = CaImporter.Import(new CaImportInput(root.RawData, "ec.cer", null, TestCa.Ascii(ec.ExportECPrivateKeyPem()), null), Now, 365);

        Assert.Empty(result.Errors);
        Assert.Equal("ECDSA P-384", CaCertificates.KeyType(result.Ca!.Certificate));
        result.Ca.Dispose();
    }

    [Fact]
    public void Import_encrypted_pkcs8_key_needs_the_right_password()
    {
        using var rsa = RSA.Create(2048);
        using var root = TestCa.Root(rsa: rsa);
        var encrypted = rsa.ExportEncryptedPkcs8PrivateKeyPem("keypass", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        CaImportResult Run(string? password) => CaImporter.Import(new CaImportInput(TestCa.Pem(root), "ca.crt", null, TestCa.Ascii(encrypted), password), Now, 365);

        Assert.Equal("The key is encrypted: enter its password.", Run(null).Errors[PkiFields.KeyPassword]);
        Assert.Equal(CaImporter.WrongKeyPassword, Run("nope").Errors[PkiFields.KeyPassword]);
        var ok = Run("keypass");
        Assert.Empty(ok.Errors);
        ok.Ca!.Dispose();
    }

    [Fact]
    public void Import_checks_key_match_ca_flag_strength_and_validity()
    {
        using var rsa = RSA.Create(2048);
        using var root = TestCa.Root(rsa: rsa);
        using var otherKey = RSA.Create(2048);
        Assert.Equal("The private key does not belong to the certificate.",
            CaImporter.Import(new CaImportInput(TestCa.Pem(root), null, null, TestCa.Ascii(otherKey.ExportPkcs8PrivateKeyPem()), null), Now, 365).Errors[PkiFields.KeyFile]);

        Assert.Equal("Choose the private key of this certificate.",
            CaImporter.Import(new CaImportInput(TestCa.Pem(root), null, null, null, null), Now, 365).Errors[PkiFields.KeyFile]);

        using var leafLike = TestCa.Root("Not a CA", ca: false);
        Assert.StartsWith("This certificate is not a CA certificate", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", leafLike), null, "pw", null, null), Now, 365).Errors[PkiFields.File], StringComparison.Ordinal);

        using var noSign = TestCa.Root("No sign", keyCertSign: false);
        Assert.Equal("This CA certificate is not allowed to sign certificates.", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", noSign), null, "pw", null, null), Now, 365).Errors[PkiFields.File]);

        using var weakKey = RSA.Create(1024);
        using var weak = TestCa.Root("Weak", rsa: weakKey);
        Assert.Equal("RSA 1024 is too weak.", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", weak), null, "pw", null, null), Now, 365).Errors[PkiFields.File]);

        using var expired = TestCa.Root("Old", years: 2, notBefore: Now.AddYears(-3));
        Assert.StartsWith("This CA expired on ", CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", expired), null, "pw", null, null), Now, 365).Errors[PkiFields.File], StringComparison.Ordinal);

        // 18 months left: certificates of 365 days would outlive the "one year beyond" margin.
        using var shortLived = TestCa.Root("Short", years: 2, notBefore: Now.AddMonths(-6));
        var tooShort = CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", shortLived), null, "pw", null, null), Now, 365).Errors[PkiFields.File];
        Assert.Equal($"This CA expires on {shortLived.NotAfter.ToUniversalTime():yyyy-MM-dd}, before certificates it would issue.", tooShort);
        Assert.Empty(CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", shortLived), null, "pw", null, null), Now, 30).Errors);
    }

    [Fact]
    public void Import_refuses_unsupported_curves_large_files_and_garbage()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var request = new CertificateRequest("CN=P521", ec, HashAlgorithmName.SHA512);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(10));
        Assert.Equal(CaImporter.UnsupportedKey, CaImporter.Import(new CaImportInput(TestCa.Pfx("pw", root), null, "pw", null, null), Now, 365).Errors[PkiFields.File]);

        Assert.Equal("The file is larger than 1 MB.", CaImporter.Import(new CaImportInput(new byte[PkiPluginInfo.MaxImportBytes + 1], null, null, null, null), Now, 365).Errors[PkiFields.File]);
        Assert.Equal(CaImporter.WrongPassword, CaImporter.Import(new CaImportInput([1, 2, 3, 4], "x.pfx", null, null, null), Now, 365).Errors[PkiFields.File]);
        Assert.Equal("The file contains no certificate.", CaImporter.Import(new CaImportInput(TestCa.Ascii("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n"), null, null, null, null), Now, 365).Errors[PkiFields.File]);
    }

    [Fact]
    public void Needs_key_file_detects_certificates_without_a_key()
    {
        using var rsa = RSA.Create(2048);
        using var root = TestCa.Root(rsa: rsa);

        Assert.True(CaImporter.NeedsKeyFile(TestCa.Pem(root)));
        Assert.True(CaImporter.NeedsKeyFile(root.RawData));
        Assert.False(CaImporter.NeedsKeyFile(TestCa.Ascii(CaCertificates.ToPem(root) + rsa.ExportPkcs8PrivateKeyPem())));
        Assert.False(CaImporter.NeedsKeyFile(TestCa.Pfx("pw", root)));
    }

    [Fact]
    public void Helpers_format_fingerprints_and_file_names()
    {
        Assert.Equal("3F:A2:0B", CaCertificates.ColonFingerprint("3FA20B"));
        Assert.Equal("OADM Root CA SERVER01.crt", CaCertificates.FileNameFor("OADM Root CA SERVER01", ".crt"));
        Assert.Equal("Acme_Sub_CA_.cer", CaCertificates.FileNameFor("Acme/Sub:CA?", ".cer"));
        Assert.Equal("ca.pfx", CaCertificates.FileNameFor("  ", ".pfx"));
    }
}
