using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

public sealed class CertificateTrustTests
{
    [Fact]
    public void SelfSignedCertificateIsSelfSigned()
    {
        using var cert = TestCertificates.SelfSigned();

        Assert.Equal(CertificateTrust.SelfSigned, CertificateTrustEvaluator.EvaluateChain(cert));
        Assert.True(CertificateTrustEvaluator.IsSelfIssued(cert));
    }

    [Fact]
    public void LeafOfAnUntrustedCaIsUntrusted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);

        // Without the CA (partial chain) and with the CA presented by the device (untrusted root).
        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf));
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);
        Assert.Equal(CertificateTrust.Untrusted, CertificateTrustEvaluator.EvaluateChain(leaf, [leaf, caPublic]));
    }

    [Fact]
    public void LeafOfATrustedRootIsTrusted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);

        var trust = CertificateTrustEvaluator.EvaluateChain(leaf, customRoots: [caPublic]);

        Assert.Equal(CertificateTrust.Trusted, trust);
    }

    [Fact]
    public void ExpiredCertificateKeepsItsChainResultButIsExpiredNow()
    {
        var notAfter = DateTimeOffset.UtcNow.AddDays(-3);
        using var cert = TestCertificates.SelfSigned(notBefore: notAfter.AddYears(-1), notAfter: notAfter);

        var info = CertificateTrustEvaluator.Describe(cert, host: "10.0.0.48");

        Assert.Equal(CertificateTrust.SelfSigned, info.ChainTrust);
        Assert.Equal(CertificateTrust.Expired, info.TrustAt(DateTimeOffset.UtcNow));
        Assert.Equal(CertificateTrust.SelfSigned, info.TrustAt(notAfter.AddDays(-1)));
    }

    [Fact]
    public void ExpiredLeafOfATrustedRootIsExpired()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca, days: 5);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);

        var info = CertificateTrustEvaluator.Describe(leaf, customRoots: [caPublic]);

        Assert.Equal(CertificateTrust.Trusted, info.TrustAt(DateTimeOffset.UtcNow));
        Assert.Equal(CertificateTrust.Expired, info.TrustAt(DateTimeOffset.UtcNow.AddDays(6)));
    }

    [Fact]
    public void DescribeCapturesSubjectIssuerValidityAndNameMatch()
    {
        using var ca = TestCertificates.CreateCa("Site CA");
        using var leaf = TestCertificates.IssuedBy(ca, cn: "cam1.example", ip: "10.0.0.48");

        var byIp = CertificateTrustEvaluator.Describe(leaf, host: "10.0.0.48");
        var byName = CertificateTrustEvaluator.Describe(leaf, host: "cam1.example");
        var other = CertificateTrustEvaluator.Describe(leaf, host: "10.0.0.49");

        Assert.Equal("CN=cam1.example", byIp.Subject);
        Assert.Equal("CN=Site CA", byIp.Issuer);
        Assert.Equal(leaf.NotAfter.ToUniversalTime(), byIp.NotAfterUtc);
        Assert.Equal(DateTimeKind.Utc, byIp.NotAfterUtc.Kind);
        Assert.Matches("^[0-9A-F]{64}$", byIp.Fingerprint);
        Assert.True(byIp.NameMatches);
        Assert.True(byName.NameMatches);
        Assert.False(other.NameMatches);
        Assert.Null(CertificateTrustEvaluator.Describe(leaf).NameMatches);
    }

    [Fact]
    public void PinningRecordsTheCertificateWithoutChangingThePinDecision()
    {
        using var first = TestCertificates.SelfSigned();
        using var second = TestCertificates.SelfSigned();

        var tofu = new CertificatePinning();
        Assert.True(tofu.Validate(first, null, "10.0.0.48"));
        Assert.Equal(CertificateTrust.SelfSigned, tofu.ObservedCertificate!.ChainTrust);
        Assert.Equal(tofu.ObservedFingerprint, tofu.ObservedCertificate.Fingerprint);

        var pinned = new CertificatePinning(CertificatePinning.ComputeFingerprint(first));
        Assert.True(pinned.Validate(first, null, "10.0.0.48"));
        Assert.False(pinned.Validate(second, null, "10.0.0.48"));
        Assert.Equal(CertificatePinning.ComputeFingerprint(second), pinned.MismatchFingerprint);
        Assert.Equal(CertificatePinning.ComputeFingerprint(second), pinned.ObservedCertificate!.Fingerprint);
    }

    [Fact]
    public void CustomRootsOfThePinningAreUsedForTheDescription()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.IssuedBy(ca);
        using var caPublic = X509CertificateLoader.LoadCertificate(ca.RawData);

        var pinning = new CertificatePinning { CustomTrustRoots = [caPublic] };
        Assert.True(pinning.Validate(leaf, null, "10.0.0.48"));

        Assert.Equal(CertificateTrust.Trusted, pinning.ObservedCertificate!.ChainTrust);
        Assert.True(pinning.ObservedCertificate.NameMatches);
    }
}
