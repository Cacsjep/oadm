using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Testing;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Trusted CAs (docs/spec/radius-server.md "Who passes", docs/spec/pki.md "Services select CAs"): the default selection,
/// PKI's "Used by", a CA left out or gone refuses at once, an imported CA without "Only devices OADM manages".
/// </summary>
public sealed class TrustedCaTests
{
    private const string Serial = "ACCC8E123456";

    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static async Task<LogRow> LastLogRowAsync(PluginHarness harness)
    {
        harness.Tick();
        return (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];
    }

    [Fact]
    public async Task TheOadmCaWithItsPreviousOnesAndTheAxisDeviceIdAreTrustedByDefaultAndPkiKnows()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        using var previous = new TestAuthority("Previous OADM CA");
        harness.Ca.Previous.Add([previous.CaCertificate]);
        harness.Ca.RaiseChanged();

        var list = await harness.CallAsync<TrustedCaList>(RadiusMethods.GetTrustedCas);

        Assert.Equal([CertificateAuthorityInfo.OadmId, CertificateAuthorityInfo.AxisDeviceIdId], list.Cas.Select(c => c.Id));
        Assert.All(list.Cas, c => Assert.True(c.Trusted && c.OnlyManaged));
        Assert.Equal(1, list.Cas[0].PreviousCount);
        await Eventually.TrueAsync(() => harness.Ca.UsedBy.LastOrDefault().Ids?.Count == 3, "PKI told about the previous CA too");
        var used = harness.Ca.UsedBy.Last();
        Assert.Equal(("oadm.radius", "802.1X server"), (used.Service, used.Name));
        Assert.Contains(CaTrust.PreviousId(previous.CaCertificate), used.Ids);
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, previous.Camera(Serial)))).Final);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial)))).Final);
    }

    [Fact]
    public async Task ACaLeftOutRefusesItsCertificatesAtOnce()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        var saved = await harness.CallAsync<TrustedCaList>(RadiusMethods.SaveTrustedCas, new TrustedCaSettings { Cas = [new TrustedCaChoice(CertificateAuthorityInfo.OadmId)] });

        Assert.False(saved.Cas.Single(c => c.Id == CertificateAuthorityInfo.AxisDeviceIdId).Trusted);
        Assert.Equal([CertificateAuthorityInfo.OadmId], harness.Ca.UsedBy.Last().Ids);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.DeviceIds.Device(Serial)))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);

        var unknown = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CallAsync<TrustedCaList>(RadiusMethods.SaveTrustedCas, new TrustedCaSettings { Cas = [new TrustedCaChoice("imported:nope")] }));
        Assert.Equal("cas", unknown.ParamName);
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

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, pc))).Final); // not checked yet
        await harness.CallAsync<TrustedCaList>(RadiusMethods.SaveTrustedCas, new TrustedCaSettings
        {
            Cas = [new TrustedCaChoice(CertificateAuthorityInfo.OadmId), new TrustedCaChoice("imported:contoso", OnlyManaged: false)],
        });

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
    public async Task AnImportedEnterpriseRootNeverLetsASiblingCaIn()
    {
        // The OADM CA sits under an enterprise root that is also imported and checked: only certificates the root issued
        // itself pass through it, never those of another CA under it.
        using var root = new TestAuthority("Enterprise root");
        var oadm = new TestAuthority("OADM CA", root);
        using var sibling = new TestAuthority("Sibling CA", root);
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial), authority: oadm);
        oadm.AuthorityList = [oadm.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm), root.Info("imported:root", CertificateAuthorityKind.Imported)];
        oadm.RaiseChanged();
        await harness.CallAsync<TrustedCaList>(RadiusMethods.SaveTrustedCas, new TrustedCaSettings
        {
            Cas = [new TrustedCaChoice(CertificateAuthorityInfo.OadmId), new TrustedCaChoice("imported:root", OnlyManaged: false)],
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, oadm.Camera(Serial)))).Final);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, sibling.Camera("ACCC8E999999")))).Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
    }
}
