using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>MS-MPPE keys (RFC 2548 2.4.2, 2.4.3), VLAN attributes (RFC 3580 3.31), TLS settings and the server identity.</summary>
public sealed class KeysAndAttributesTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("xyzzy5461");
    private static readonly byte[] RequestAuthenticator = Hex.Bytes("0f 40 3f 94 73 97 80 57 bd 83 d5 cb 98 f4 22 7a");

    [Fact]
    public void TheMppeEncryptionFollowsTheRfcConstruction()
    {
        // RFC 2548 2.4.2: P = Key-Length + Key, zero-padded to 16; b(1) = MD5(S + R + A), c(1) = p(1) xor b(1),
        // b(i) = MD5(S + c(i-1)), c(i) = p(i) xor b(i). Computed here step by step, independently of MppeKeys.
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        byte[] salt = [0x80, 0x01];
        var plain = new byte[48];
        plain[0] = 32;
        key.CopyTo(plain, 1);
        var expected = new byte[48];
        var b = MD5.HashData([.. Secret, .. RequestAuthenticator, .. salt]);
        for (var block = 0; block < 3; block++)
        {
            for (var i = 0; i < 16; i++)
            {
                expected[(block * 16) + i] = (byte)(plain[(block * 16) + i] ^ b[i]);
            }

            b = MD5.HashData([.. Secret, .. expected.AsSpan(block * 16, 16).ToArray()]);
        }

        var cipher = MppeKeys.Encrypt(key, Secret, RequestAuthenticator, salt);

        Assert.Equal(expected, cipher);
        Assert.Equal(key, MppeKeys.Decrypt(cipher, Secret, RequestAuthenticator, salt));
        Assert.NotEqual(key, MppeKeys.Decrypt(cipher, Encoding.ASCII.GetBytes("other"), RequestAuthenticator, salt));
    }

    [Fact]
    public void TheMsksHalvesGoIntoRecvAndSendKeyWithDistinctSalts()
    {
        // RFC 5216 2.3: Enc-RECV-Key = MSK(0,31) = MS-MPPE-Recv-Key, Enc-SEND-Key = MSK(32,63) = MS-MPPE-Send-Key.
        var msk = RandomNumberGenerator.GetBytes(64);
        var attributes = MppeKeys.Attributes(msk, Secret, RequestAuthenticator);

        Assert.Equal(2, attributes.Count);
        Assert.All(attributes, a =>
        {
            Assert.Equal(RadiusAttributeType.VendorSpecific, a.Type);
            Assert.Equal(56, a.Value.Length); // vendor id 4 + type 1 + length 1 + salt 2 + 48 encrypted
            Assert.Equal(new byte[] { 0, 0, 0x01, 0x37 }, a.Value[..4].ToArray()); // 311
            Assert.Equal(52, a.Value.Span[5]);
            Assert.True((a.Value.Span[6] & 0x80) != 0);
        });
        Assert.Equal(MppeKeys.RecvKeyType, attributes[0].Value.Span[4]);
        Assert.Equal(MppeKeys.SendKeyType, attributes[1].Value.Span[4]);
        Assert.NotEqual(attributes[0].Value[6..8].ToArray(), attributes[1].Value[6..8].ToArray());

        var recv = MppeKeys.Decrypt(attributes[0].Value.Span[8..], Secret, RequestAuthenticator, attributes[0].Value.Span[6..8]);
        var send = MppeKeys.Decrypt(attributes[1].Value.Span[8..], Secret, RequestAuthenticator, attributes[1].Value.Span[6..8]);
        Assert.Equal(msk[..32], recv);
        Assert.Equal(msk[32..], send);
    }

    [Fact]
    public void VlanAttributesUseTagZeroAndTheIdAsText()
    {
        var attributes = VlanAttributes.Attributes(20);

        Assert.Equal([RadiusAttributeType.TunnelType, RadiusAttributeType.TunnelMediumType, RadiusAttributeType.TunnelPrivateGroupId], attributes.Select(a => a.Type));
        Assert.Equal(new byte[] { 0, 0, 0, 13 }, attributes[0].Value.ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 6 }, attributes[1].Value.ToArray());
        Assert.Equal(new byte[] { 0, (byte)'2', (byte)'0' }, attributes[2].Value.ToArray());
        Assert.Equal(new byte[] { 0, (byte)'4', (byte)'0', (byte)'9', (byte)'4' }, VlanAttributes.Attributes(4094)[2].Value.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => VlanAttributes.Attributes(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VlanAttributes.Attributes(4095));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientCertificateVerdict.Accept(4095));
    }

    [Fact]
    public void TheDefaultTlsSettingsAreTls12And13WithTheSecureSuites()
    {
        var settings = TlsSettings.Default;

        Assert.Equal(TlsVersion.Tls12, settings.MinimumVersion);
        Assert.Equal([TlsVersion.Tls13, TlsVersion.Tls12], settings.Versions);
        Assert.Null(settings.Validate());
        Assert.Equal(9, settings.CipherSuites.Count);
        Assert.DoesNotContain(settings.CipherSuites, id => TlsCipherSuites.Find(id)!.Group == CipherSuiteGroup.LegacyCbc);
        Assert.Equal(
            [CipherSuite.TLS_AES_128_GCM_SHA256, CipherSuite.TLS_AES_256_GCM_SHA384, CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
             CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384, CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256],
            settings.Offered(ServerKeyKind.Ecdsa));
    }

    [Fact]
    public void TheOldCbcSuitesAreListedAndCheckedOnlyWhenTheMinimumIsLowered()
    {
        Assert.Equal(9, TlsCipherSuites.Listed(TlsVersion.Tls12).Count);
        Assert.Equal(9, TlsCipherSuites.Listed(TlsVersion.Tls13).Count);
        Assert.Equal(13, TlsCipherSuites.Listed(TlsVersion.Tls10).Count);
        Assert.Equal(13, TlsCipherSuites.DefaultFor(TlsVersion.Tls11).Count);

        // A checked CBC suite is not offered while the minimum is TLS 1.2.
        var settings = new TlsSettings(TlsVersion.Tls12, TlsCipherSuites.DefaultFor(TlsVersion.Tls10));
        Assert.DoesNotContain(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA, settings.Offered(ServerKeyKind.Ecdsa));
        var lowered = settings with { MinimumVersion = TlsVersion.Tls10 };
        Assert.Contains(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA, lowered.Offered(ServerKeyKind.Ecdsa));
        Assert.Equal([TlsVersion.Tls13, TlsVersion.Tls12, TlsVersion.Tls11, TlsVersion.Tls10], lowered.Versions);
    }

    [Fact]
    public void TheCatalogueHasNoWeakSuites()
    {
        // Never RC4, 3DES, NULL, export, anonymous or RSA key exchange (RFC 9325 4.1); only ECDHE in TLS 1.2.
        Assert.All(TlsCipherSuites.All, s =>
        {
            Assert.DoesNotContain("RC4", s.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("3DES", s.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("NULL", s.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("EXPORT", s.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("anon", s.Name, StringComparison.Ordinal);
            Assert.True(s.Group == CipherSuiteGroup.Tls13 || s.Name.StartsWith("TLS_ECDHE_", StringComparison.Ordinal), s.Name);
        });
        Assert.Equal("Unknown cipher suite.", new TlsSettings(TlsVersion.Tls12, new HashSet<int> { CipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA }).Validate());
        Assert.Equal("Unknown cipher suite.", new TlsSettings(TlsVersion.Tls12, new HashSet<int> { CipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256 }).Validate());
    }

    [Fact]
    public void AtLeastOneUsableSuiteMustStayChecked()
    {
        Assert.Equal("Check at least one cipher suite for the chosen TLS versions.", new TlsSettings(TlsVersion.Tls12, new HashSet<int>()).Validate());
        // Only CBC suites with a minimum of TLS 1.2: they are not listed, so nothing is usable.
        Assert.NotNull(new TlsSettings(TlsVersion.Tls12, new HashSet<int> { CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA }).Validate());
        // Only TLS 1.2 suites with a minimum of TLS 1.3.
        Assert.NotNull(new TlsSettings(TlsVersion.Tls13, new HashSet<int> { CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 }).Validate());
        Assert.Null(new TlsSettings(TlsVersion.Tls13, new HashSet<int> { CipherSuite.TLS_AES_256_GCM_SHA384 }).Validate());
    }

    [Fact]
    public void TheServerIdentityIsMadeFromDerAndPkcs8OrFromADotNetCertificate()
    {
        var ec = TestPki.ServerIdentity;
        Assert.Equal(ServerKeyKind.Ecdsa, ec.Kind);
        Assert.Equal(256, ec.KeyBits);
        Assert.Equal(2, ec.Chain.Count);

        var rsa = TestPki.ServerIdentityRsa;
        Assert.Equal(ServerKeyKind.Rsa, rsa.Kind);
        Assert.Equal(2048, rsa.KeyBits);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=oadm", key, HashAlgorithmName.SHA384);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var exportable = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
        var fromDotNet = TlsServerIdentity.FromCertificate(exportable);
        Assert.Equal(384, fromDotNet.KeyBits);
        Assert.Single(fromDotNet.Chain);

        using var withoutKey = X509CertificateLoader.LoadCertificate(certificate.RawData);
        Assert.Throws<ArgumentException>(() => TlsServerIdentity.FromCertificate(withoutKey));
        Assert.Throws<ArgumentException>(() => TlsServerIdentity.Create([], TestPki.Server.Pkcs8));
    }
}
