using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>A certificate an OADM CA renewed replaces the pin without Certificate changed (docs/spec/est-server.md).</summary>
public sealed class CertificateRenewalTests
{
    private const string Serial = "B8A44F631339";
    private const string DeviceName = "axis-b8a44f631339.local";

    [Fact]
    public void RenewalByAnOadmCaIsAcceptedAndPinnedAgain()
    {
        using var ca = TestCertificates.CreateCa();
        using var old = TestCertificates.SelfSigned();
        using var renewed = TestCertificates.IssuedBy(ca, DeviceName);
        var pinning = Pinning(ca, CertificatePinning.ComputeFingerprint(old));

        Assert.True(pinning.Validate(renewed, null, "10.0.0.48"));
        Assert.Null(pinning.MismatchFingerprint);
        Assert.Equal(CertificatePinning.ComputeFingerprint(renewed), pinning.RenewedFingerprint);
        Assert.Equal(pinning.RenewedFingerprint, pinning.ObservedFingerprint);
    }

    [Fact]
    public void ThePinnedCertificateIsNoRenewal()
    {
        using var ca = TestCertificates.CreateCa();
        using var current = TestCertificates.IssuedBy(ca, DeviceName);
        var pinning = Pinning(ca, CertificatePinning.ComputeFingerprint(current));

        Assert.True(pinning.Validate(current, null, "10.0.0.48"));
        Assert.Null(pinning.RenewedFingerprint);
    }

    [Fact]
    public void AnotherDeviceNameIsCertificateChanged()
    {
        using var ca = TestCertificates.CreateCa();
        using var old = TestCertificates.SelfSigned();
        using var otherDevice = TestCertificates.IssuedBy(ca, "axis-accc8e000001.local");
        var pinning = Pinning(ca, CertificatePinning.ComputeFingerprint(old));

        Assert.False(pinning.Validate(otherDevice, null, "10.0.0.48"));
        Assert.Equal(CertificatePinning.ComputeFingerprint(otherDevice), pinning.MismatchFingerprint);
        Assert.Null(pinning.RenewedFingerprint);
    }

    [Fact]
    public void AnotherCaIsCertificateChanged()
    {
        using var ca = TestCertificates.CreateCa();
        using var foreign = TestCertificates.CreateCa("Other CA");
        using var old = TestCertificates.SelfSigned();
        using var renewed = TestCertificates.IssuedBy(foreign, DeviceName);
        var pinning = Pinning(ca, CertificatePinning.ComputeFingerprint(old));

        Assert.False(pinning.Validate(renewed, null, "10.0.0.48"));
        Assert.Null(pinning.RenewedFingerprint);
    }

    [Fact]
    public void AnExpiredCertificateIsCertificateChanged()
    {
        using var ca = TestCertificates.CreateCa();
        using var old = TestCertificates.SelfSigned();
        using var expired = TestCertificates.IssuedBy(ca, DeviceName, days: 10);
        var pinning = new CertificatePinning(CertificatePinning.ComputeFingerprint(old))
        {
            TrustAnchors = Anchors(ca),
            DeviceSerial = Serial,
            Time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(30)),
        };

        Assert.False(pinning.Validate(expired, null, "10.0.0.48"));
    }

    [Fact]
    public void WithoutTheDeviceSerialEveryOtherCertificateIsChanged()
    {
        using var ca = TestCertificates.CreateCa();
        using var old = TestCertificates.SelfSigned();
        using var renewed = TestCertificates.IssuedBy(ca, DeviceName);
        var pinning = new CertificatePinning(CertificatePinning.ComputeFingerprint(old)) { TrustAnchors = Anchors(ca) };

        Assert.False(pinning.Validate(renewed, null, "10.0.0.48"));
    }

    private static CertificatePinning Pinning(X509Certificate2 ca, string pin) =>
        new(pin) { TrustAnchors = Anchors(ca), DeviceSerial = Serial };

    private static TrustAnchorRegistry Anchors(X509Certificate2 ca)
    {
        var anchors = new TrustAnchorRegistry();
        anchors.Set("oadm.pki", [ca.RawData]);
        return anchors;
    }
}
