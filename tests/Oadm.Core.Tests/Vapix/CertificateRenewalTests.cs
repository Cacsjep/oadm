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
    public void ACertificateNamingASecondCameraIsCertificateChanged()
    {
        // A camera that got its certificate to name another camera too must never be taken for that camera.
        using var ca = TestCertificates.CreateCa();
        using var old = TestCertificates.SelfSigned();
        using var both = TestCertificates.IssuedByWithNames(ca, "axis-accc8e000001.local", DeviceName);
        var pinning = Pinning(ca, CertificatePinning.ComputeFingerprint(old));

        Assert.False(pinning.Validate(both, null, "10.0.0.48"));
        Assert.Null(pinning.RenewedFingerprint);
        Assert.Equal("B8A44F631339", CertificatePinning.AxisLocalSerial("AXIS-b8a44f631339.local."));
        Assert.Null(CertificatePinning.AxisLocalSerial("axis-b8a44f6313390.local"));
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
    public void OnlyThePkiCaItselfVouchesForARenewal()
    {
        using var root = TestCertificates.CreateCa("Customer Root CA");
        using var oadmCa = TestCertificates.CreateIntermediate(root, "OADM CA");
        using var otherCa = TestCertificates.CreateIntermediate(root, "Other issuing CA");
        using var old = TestCertificates.SelfSigned();
        using var fromOadm = TestCertificates.IssuedBy(oadmCa, DeviceName);
        using var fromSibling = TestCertificates.IssuedBy(otherCa, DeviceName);
        using var fromRoot = TestCertificates.IssuedBy(root, DeviceName);
        var anchors = new TrustAnchorRegistry();
        // The PKI rates certificates with its CA and the root above it, but only its CA vouches for renewals.
        anchors.Set(TrustAnchorRegistry.RenewalOwner, [oadmCa.RawData, root.RawData]);
        anchors.SetRenewalIssuers(TrustAnchorRegistry.RenewalOwner, [oadmCa.RawData]);
        CertificatePinning Fresh() => new(CertificatePinning.ComputeFingerprint(old)) { TrustAnchors = anchors, DeviceSerial = Serial };

        Assert.True(Fresh().Validate(fromOadm, null, "10.0.0.48"));
        Assert.False(Fresh().Validate(fromSibling, [otherCa], "10.0.0.48")); // another CA under the same root
        Assert.False(Fresh().Validate(fromRoot, null, "10.0.0.48")); // the root itself
    }

    [Fact]
    public void AnotherPluginsCertificatesNeverReplaceAPin()
    {
        using var ca = TestCertificates.CreateCa("Some plugin's CA");
        using var old = TestCertificates.SelfSigned();
        using var renewed = TestCertificates.IssuedBy(ca, DeviceName);
        var anchors = new TrustAnchorRegistry();
        anchors.Set("acme.plugin", [ca.RawData]);
        anchors.SetRenewalIssuers("acme.plugin", [ca.RawData]);
        var pinning = new CertificatePinning(CertificatePinning.ComputeFingerprint(old)) { TrustAnchors = anchors, DeviceSerial = Serial };

        Assert.False(pinning.Validate(renewed, null, "10.0.0.48"));
        Assert.Empty(anchors.Current.RenewalIssuers);
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
        anchors.Set(TrustAnchorRegistry.RenewalOwner, [ca.RawData]);
        anchors.SetRenewalIssuers(TrustAnchorRegistry.RenewalOwner, [ca.RawData]);
        return anchors;
    }
}
