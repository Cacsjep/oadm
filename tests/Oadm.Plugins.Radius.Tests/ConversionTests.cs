using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The 802.1X server's settings from before the policies become PKI's policies once, at the first start (docs/spec/pki.md
/// "Converting existing settings"), from fixtures of every stored format: the VLAN rules of early builds ("4.1" in the code),
/// Advanced, Trusted CAs and the policies of development builds with their Default VLAN. Nothing is dropped, overlaps are
/// kept with their warning, a second start changes nothing.
/// </summary>
public sealed class ConversionTests
{
    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static readonly Guid FixtureSite = new("7d1f3f6e-2b7c-4b55-9c3e-5a1f6c0a9b01");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Conversion", name));

    private static async Task<PluginHarness> StoredAsync(params (string Key, string File)[] settings)
    {
        var harness = await PluginHarness.CreateAsync(start: false);
        foreach (var (key, file) in settings)
        {
            await harness.Ctx.PluginSettings.SetAsync(key, Fixture(file), CancellationToken.None);
        }

        return harness;
    }

    [Fact]
    public async Task TheVlanRulesOfEarlyBuildsBecomeVlanPoliciesWithTheDefaultVlanLastForEveryDevice()
    {
        await using var harness = await StoredAsync((RadiusStore.VlansKey, "vlans-early.json"));
        var site = harness.Ctx.FakeSites.Add("Building B");
        harness.AddDevice("ACCC8E000001"); // a device of this server: in "This server" and "All devices"

        await harness.StartAsync();

        var vlans = harness.Policies.PoliciesOf(PolicyKinds.Vlan);
        Assert.Equal([("PTZ", 20), ("This server", 30), ("PTZ (2)", 40), (PolicyLimits.AllDevicesName, 99)], vlans.Select(p => (p.Name, p.Vlan!.Value)));
        Assert.Equal(["PTZ"], vlans[0].Devices!.Tags);
        Assert.True(vlans[1].Devices!.ThisServer);
        Assert.Equal([FixtureSite], vlans[2].Devices!.SiteIds);
        Assert.True(vlans[3].Devices!.AllDevices); // every device, also of sites added later: no device changes its VLAN
        Assert.Empty(vlans[3].Devices!.SiteIds);
        Assert.NotEqual(Guid.Empty, site.Id);

        // The overlaps the conversion made are kept and show their warning in both rows; the older applies.
        var rows = harness.Policies.Rows([.. harness.Ctx.DeviceList]);
        var warning = "All devices includes every device, also those of This server. This server applies.";
        Assert.Equal(warning, rows.Single(r => r.Policy.Name == "This server").Warning);
        Assert.Contains(warning, rows.Single(r => r.Policy.Name == PolicyLimits.AllDevicesName).Warning);

        // The old settings stay as they were: nothing is deleted.
        Assert.NotNull(await harness.Ctx.PluginSettings.GetAsync(RadiusStore.VlansKey, CancellationToken.None));
    }

    [Fact]
    public async Task VlanRulesThatWereOffMakeNoVlanPolicy()
    {
        await using var harness = await StoredAsync((RadiusStore.VlansKey, "vlans-early-off.json"));

        await harness.StartAsync();

        Assert.Empty(harness.Policies.PoliciesOf(PolicyKinds.Vlan));
        Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Dot1xServer)); // the defaults of the other kinds
    }

    [Fact]
    public async Task TheSettingsOfDevelopmentBuildsKeepTheirValuesNamesDevicesAndOrder()
    {
        await using var harness = await StoredAsync(
            (RadiusStore.ConfigKey, "config-advanced.json"),
            (RadiusStore.TrustedCasKey, "trusted-cas.json"),
            (RadiusStore.PoliciesKey, "policies-main.json"));
        using var company = new TestAuthority("Contoso Root");
        harness.Ca.AuthorityList = [harness.Ca.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm, "OADM Root CA"), harness.DeviceIds.Info,
            company.Info("imported:contoso", CertificateAuthorityKind.Imported, "Contoso Root")];
        harness.Ctx.FakeSites.Add("Building B");

        await harness.StartAsync();

        var server = Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Dot1xServer));
        Assert.Equal(("1.3", 1200, (int?)null), (server.MinimumTls, server.LargestEapPacket!.Value, server.ReauthMinutes));
        Assert.Equal([4865, 4866], server.CipherSuites);
        Assert.Equal([("OADM CA", CertificateAuthorityInfo.OadmId, true), ("Contoso Root", "imported:contoso", false)],
            harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Select(p => (p.Name, p.CaId!, p.OnlyManaged!.Value))); // one per checked CA
        Assert.Equal(["Building B", "PTZ", PolicyLimits.AllDevicesName], harness.Policies.PoliciesOf(PolicyKinds.Vlan).Select(p => p.Name));
        Assert.Equal([FixtureSite], harness.Policies.PoliciesOf(PolicyKinds.Vlan)[0].Devices!.SiteIds);
        Assert.Equal(new Guid("5b0e7c1a-3f53-4d2b-9a8e-1d2c3b4a5f60"), harness.Policies.PoliciesOf(PolicyKinds.Vlan)[0].Id);
        Assert.Equal(99, harness.Policies.PoliciesOf(PolicyKinds.Vlan)[2].Vlan);
        // A Block policy stays Block, a Revoke policy becomes Block with Also revoke (decision 2026-10-25).
        var blocks = harness.Policies.PoliciesOf(PolicyKinds.Block);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(["ACCC8E000001"], blocks[0].Devices!.Serials);
        Assert.False(blocks[0].AlsoRevoke!.Value);
        Assert.Equal(("Stolen", "admin", true), (blocks[1].Name, blocks[1].SavedBy, blocks[1].AlsoRevoke!.Value));
        Assert.Equal([PolicyKinds.Dot1xServer, PolicyKinds.AllowCa, PolicyKinds.AllowCa, PolicyKinds.Vlan, PolicyKinds.Vlan, PolicyKinds.Block, PolicyKinds.Block, PolicyKinds.Vlan],
            harness.Policies.All.Where(p => PolicyKinds.IsDot1x(p.Kind)).Select(p => p.Kind)); // the age order: the stored order, the Default VLAN last
        Assert.Equal((CertificateAuthorityInfo.OadmId, 365, PolicyKeys.Ecc), (server.CaId, server.ValidDays!.Value, server.Key)); // its certificate as before

        // The server applies them at once (TLS 1.3 only, 1,200 bytes).
        Assert.Equal(Protocol.Tls.TlsVersion.Tls13, harness.Service.Processor.Options.Tls.MinimumVersion);
        Assert.Equal(1200, harness.Service.Processor.Options.LargestFragment);
    }

    [Fact]
    public async Task TheConvertedDefaultVlanStillReachesDevicesOadmDoesNotManageAndDevicesOfSitesAddedLater()
    {
        await using var harness = await StoredAsync((RadiusStore.TrustedCasKey, "trusted-cas.json"), (RadiusStore.PoliciesKey, "policies-main.json"));
        using var company = new TestAuthority("Contoso Root");
        harness.Ca.AuthorityList = [harness.Ca.Info(CertificateAuthorityInfo.OadmId, CertificateAuthorityKind.Oadm, "OADM Root CA"), harness.DeviceIds.Info,
            company.Info("imported:contoso", CertificateAuthorityKind.Imported, "Contoso Root")];
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        // A certificate of the Allow CA for any device, naming no device OADM manages: the Default VLAN gave it 99, so does "All devices".
        var unmanaged = await harness.Switch().RunAsync(Camera(harness, company.Camera("ACCC8E999999")));
        Assert.Equal(RadiusCode.AccessAccept, unmanaged.Final);
        Assert.Equal(99, PluginSwitch.VlanOf(unmanaged.LastAnswer!));

        // A site bound after the conversion: its devices are in "All devices" too.
        var site = harness.Ctx.FakeSites.Add("Building C");
        site.AllowedPorts.Add((SiteProtocol.Udp, 1812));
        site.Network.LocalAddress = System.Net.IPAddress.Parse("10.2.0.5");
        harness.AddDevice("ACCC8E777777", site.Id);
        await harness.RefreshDevicesAsync();
        await harness.AddSwitchAsync("Site switch", "192.168.2.2", site.Id);
        var relay = (InMemoryUdpRelay)harness.Service.SiteRelay(site.Id)!;
        var atSite = await harness.Switch("192.168.2.2", relay: relay).RunAsync(Camera(harness, harness.Ca.Camera("ACCC8E777777")));
        Assert.Equal(RadiusCode.AccessAccept, atSite.Final);
        Assert.Equal(99, PluginSwitch.VlanOf(atSite.LastAnswer!));
    }

    [Fact]
    public async Task TheConversionRunsOnceAndASecondStartChangesNothing()
    {
        await using var harness = await StoredAsync((RadiusStore.PoliciesKey, "policies-main.json"));
        await harness.StartAsync();
        var first = harness.Policies.All.Select(p => p.Id).ToList();
        await harness.Policies.DeleteAsync(harness.Policies.PoliciesOf(PolicyKinds.Block)[0].Id, "admin", CancellationToken.None);

        await harness.Plugin.StopAsync(CancellationToken.None);
        await harness.StartAsync();

        Assert.Equal(first.Count - 1, harness.Policies.All.Count); // the deleted one stays deleted, nothing doubled
        Assert.True(Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Block)).AlsoRevoke); // the converted Revoke policy stays
        Assert.Contains(RadiusPlugin.PluginId, harness.Policies.Converted);

        // PKI's store read again from its settings (a restart of PKI) keeps the same list.
        using var again = new PolicyStore(harness.PkiSettings, _ => Task.FromResult<IReadOnlyList<Sdk.Devices.IDeviceInfo>>([]), () => [], () => [], null);
        await again.LoadAsync(CancellationToken.None);
        Assert.Equal(harness.Policies.All.Select(p => p.Id), again.All.Select(p => p.Id));
        Assert.False(await again.ConvertOnceAsync(RadiusPlugin.PluginId, _ => throw new InvalidOperationException("converted twice"), CancellationToken.None));
    }

    [Fact]
    public async Task UnreadableOrMissingPartsUseTheDefaultsOfTheirKind()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        await harness.Ctx.PluginSettings.SetAsync(RadiusStore.ConfigKey, "{\"port\":1812,\"minimumTls\":\"9.9\",\"cipherSuites\":[],\"largestFragment\":99999}", CancellationToken.None);
        await harness.Ctx.PluginSettings.SetAsync(RadiusStore.PoliciesKey, "not json", CancellationToken.None);

        await harness.StartAsync();

        var server = Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Dot1xServer));
        Assert.Equal(("1.2", 1400), (server.MinimumTls, server.LargestEapPacket!.Value));
        Assert.Equal(RadiusPolicies.DefaultServerPolicy().CipherSuites, server.CipherSuites);
        Assert.Equal(2, harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Count);
    }
}
