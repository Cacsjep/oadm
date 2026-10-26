using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Testing;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Allow CA policies (docs/spec/pki.md "Policies", docs/spec/radius-server.md "Who passes"): the defaults, a CA without a
/// policy or gone refuses at once, an imported CA without "Only devices OADM manages", an enterprise root never lets a
/// sibling CA in.
/// </summary>
public sealed class AllowCaTests
{
    private const string Serial = "ACCC8E123456";

    /// <summary>A camera that checks the 802.1X server with <paramref name="serverCa"/> (default: the harness's CA, whose certificate the server shows).</summary>
    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate, TestAuthority? serverCa = null) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = (serverCa ?? harness.Ca).CaCertificate, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static async Task<LogRow> LastLogRowAsync(PluginHarness harness)
    {
        harness.Tick();
        return (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];
    }

    [Fact]
    public async Task TheOadmCaAndTheAxisDeviceIdAreAllowedByDefaultAndAPreviousOadmCaOnlyWithItsOwnPolicy()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        using var previous = new TestAuthority("Previous OADM CA");
        harness.Ca.Previous.Add([previous.CaCertificate]);
        harness.Ca.RaiseChanged();

        Assert.Equal([CertificateAuthorityInfo.OadmId, CertificateAuthorityInfo.AxisDeviceIdId], harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Select(p => p.CaId));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        // Decision 2026-10-26: a previous OADM CA is an ordinary CA, it no longer follows the OADM CA's policy.
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, previous.Camera(Serial)))).Final);
        await harness.SavePolicyAsync(new PkiPolicy { Name = "Previous OADM CA", Kind = PolicyKinds.AllowCa, CaId = CaTrust.PreviousId(previous.CaCertificate), OnlyManaged = true });
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, previous.Camera(Serial)))).Final);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial)))).Final);
        Assert.Empty(harness.Ca.UsedBy); // PKI knows its own policies: the 802.1X server tells it nothing
    }

    [Fact]
    public void ACaChosenTwiceIsOnlyManagedWhenEitherChoiceIsWhateverTheOrder()
    {
        var oadm = new CertificateAuthorityInfo(CertificateAuthorityInfo.OadmId, "OADM CA", CertificateAuthorityKind.Oadm, [], DateTime.UtcNow.AddYears(1), true);
        var previous = new CertificateAuthorityInfo("oadm-previous:1", "OADM CA 2025", CertificateAuthorityKind.PreviousOadm, [], DateTime.UtcNow.AddYears(1), false);
        TrustedCaSettings Of(params TrustedCaChoice[] cas) => new() { Cas = cas };

        var loose = new TrustedCaChoice(CertificateAuthorityInfo.OadmId, OnlyManaged: false);
        var strict = new TrustedCaChoice("OADM", OnlyManaged: true);
        Assert.True(CaTrust.Chosen([oadm, previous], Of(loose, strict))[CertificateAuthorityInfo.OadmId]);
        Assert.False(CaTrust.Chosen([oadm, previous], Of(strict, loose)).ContainsKey("oadm-previous:1")); // previous CAs no longer follow the OADM CA (decision 2026-10-26)
        Assert.False(CaTrust.Chosen([oadm], Of(loose))[CertificateAuthorityInfo.OadmId]);
    }

    [Fact]
    public async Task ACaWithoutAPolicyRefusesItsCertificatesAtOnce()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        await harness.SetAllowCasAsync((CertificateAuthorityInfo.OadmId, true));

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial)))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);

        var unknown = await harness.Policies.SaveAsync(new PkiPolicy { Name = "Nope", Kind = PolicyKinds.AllowCa, CaId = "imported:nope" }, "admin", RadiusPolicies.CipherSuites, CancellationToken.None);
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.UnknownCa), unknown.Error);
        var twice = await harness.Policies.SaveAsync(new PkiPolicy { Name = "Again", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.OadmId }, "admin", RadiusPolicies.CipherSuites, CancellationToken.None);
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.CaTaken), twice.Error);
    }

    [Fact]
    public async Task APolicyDeletedWhileAHandshakeRebuildsTheSetNeverLetsTheCaPassAfterwards()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial)))).Final);

        // PKI offers a new CA object: the next handshake rebuilds the set from the rules it read, and the delete of the Axis
        // device ID policy lands in the middle of that rebuild.
        using var restarted = new TestAuthority("OADM CA after a restart");
        var deleted = false;
        var axis = harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Single(p => p.CaId == CertificateAuthorityInfo.AxisDeviceIdId);
        restarted.OnNextPreviousRead(() =>
        {
            Task.Run(() => harness.Policies.DeleteAsync(axis.Id, "admin", CancellationToken.None)).GetAwaiter().GetResult();
            deleted = true;
        });
        harness.Ctx.FakeServices.Offer<IDeviceCertificateAuthority>(restarted);
        await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial))); // started before the delete: either answer
        Assert.True(deleted);

        // The server shows the certificate of the CA PKI offers now: the camera checks the server with that one.
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial), restarted))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
    }

    [Fact]
    public async Task AnImportedCaForAnyDeviceLetsANonManagedCertificateInWithItsSubjectAndDropsOutWhenPkiRemovesIt()
    {
        using var company = new TestAuthority("Contoso Root");
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        var imported = company.Info("imported:contoso", CertificateAuthorityKind.Imported, "Contoso Root");
        harness.Ca.AuthorityList = [harness.Ca.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm, "OADM CA"), harness.DeviceIds.Info, imported];
        harness.Ca.RaiseChanged();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var pc = company.Camera("ACCC8E999999"); // not managed

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, pc))).Final); // no policy yet
        await harness.SavePolicyAsync(new PkiPolicy { Name = "Contoso", Kind = PolicyKinds.AllowCa, CaId = "imported:contoso", OnlyManaged = false });

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, pc))).Final);
        var row = await LastLogRowAsync(harness);
        Assert.Null(row.DeviceId);
        Assert.Equal("CN=10.0.0.48", row.DeviceName);
        Assert.Equal(LogResults.Authenticated, row.Result);

        // A certificate of that CA naming a managed device gets the device.
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, company.Camera(Serial)))).Final);
        Assert.Equal(harness.Ctx.DeviceList[0].Id, (await LastLogRowAsync(harness)).DeviceId);

        // PKI removed the CA: refused at once (fail closed).
        harness.Ca.AuthorityList = [.. harness.Ca.AuthorityList.Where(a => a.Id != imported.Id)];
        harness.Ca.RaiseChanged();
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, pc))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
    }

    [Fact]
    public async Task TheSubjectOfAnUnmanagedDevicesCertificateIsShownWithoutControlCharacters()
    {
        using var company = new TestAuthority("Contoso Root");
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        harness.Ca.AuthorityList = [harness.Ca.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm, "OADM CA"), harness.DeviceIds.Info,
            company.Info("imported:contoso", CertificateAuthorityKind.Imported, "Contoso Root")];
        harness.Ca.RaiseChanged();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.SavePolicyAsync(new PkiPolicy { Name = "Contoso", Kind = PolicyKinds.AllowCa, CaId = "imported:contoso", OnlyManaged = false });
        var name = new System.Security.Cryptography.X509Certificates.X500DistinguishedNameBuilder();
        name.AddCommonName("pc\u001b[31m-7‮");

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, company.Camera(name.Build(), "ACCC8E999999")))).Final);

        Assert.Equal("CN=pc[31m-7", (await LastLogRowAsync(harness)).DeviceName); // the device chose its subject: no control or format characters
    }

    [Fact]
    public async Task AnImportedCaThatIssuesKeepsPkisRevocationListLikeTheOadmCa()
    {
        // Decision 2026-10-25: an imported CA with its key issues certificates in OADM; a certificate OADM revoked never
        // passes, as for the OADM CA. Without its key the CA's certificates come from elsewhere: OADM's list does not apply.
        using var company = new TestAuthority("Contoso Issuing CA");
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        var issuing = company.Info("imported:contoso", CertificateAuthorityKind.Imported, "Contoso Issuing CA") with { Issues = true };
        harness.Ca.AuthorityList = [harness.Ca.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm, "OADM CA"), harness.DeviceIds.Info, issuing];
        harness.Ca.RaiseChanged();
        await harness.SavePolicyAsync(new PkiPolicy { Name = "Contoso", Kind = PolicyKinds.AllowCa, CaId = "imported:contoso", OnlyManaged = true });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var camera = company.Camera(Serial);
        harness.Ca.Revoke(TestAuthority.SerialOf(camera));

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, camera))).Final);
        Assert.Contains("revoked", (await LastLogRowAsync(harness)).Reason, StringComparison.OrdinalIgnoreCase);

        harness.Ca.AuthorityList = [.. harness.Ca.AuthorityList.Select(a => a.Id == issuing.Id ? a with { Issues = false } : a)];
        harness.Ca.RaiseChanged();
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, camera))).Final);
    }

    [Fact]
    public async Task AnImportedEnterpriseRootNeverLetsASiblingCaIn()
    {
        // The OADM CA sits under an enterprise root that is also imported and allowed: only certificates the root issued
        // itself pass through it, never those of another CA under it.
        using var root = new TestAuthority("Enterprise root");
        var oadm = new TestAuthority("OADM CA", root);
        using var sibling = new TestAuthority("Sibling CA", root);
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial), authority: oadm);
        oadm.AuthorityList = [oadm.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm), root.Info("imported:root", CertificateAuthorityKind.Imported)];
        oadm.RaiseChanged();
        await harness.SetAllowCasAsync((CertificateAuthorityInfo.OadmId, true), ("imported:root", false));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, oadm.Camera(Serial)))).Final);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, sibling.Camera("ACCC8E999999")))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
    }
}
