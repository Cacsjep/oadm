using System.Buffers.Binary;

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
/// The 802.1X server's rules are PKI's policies (docs/spec/pki.md "Policies"): VLAN (the oldest that holds a device, none
/// without one), Block, Revoke and the 802.1X server policy apply to the next handshake at once; Sessions and Log name the
/// policy; without PKI's policies nothing passes.
/// </summary>
public sealed class PolicyTests
{
    private const string Serial = "ACCC8E123456";
    private const string Other = "ACCC8E654321";

    internal static PkiPolicy Vlan(string name, int vlan, PolicyDevices devices) => new() { Name = name, Kind = PolicyKinds.Vlan, Vlan = vlan, Devices = devices };

    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static async Task<LogRow> LastLogRowAsync(PluginHarness harness)
    {
        harness.Tick();
        return (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];
    }

    [Fact]
    public async Task VlanPoliciesGiveTheirVlanAndTheLogNamesThePolicyAndADeviceOfNoPolicyGetsNone()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial, tags: ["PTZ"]);
            h.AddDevice(Other);
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var accept = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        Assert.Null(PluginSwitch.VlanOf(accept)); // no VLAN policy: no VLAN (there is no default VLAN)

        await harness.SavePolicyAsync(Vlan("Cameras with PTZ", 20, new PolicyDevices { Tags = ["ptz"] }));
        await harness.SavePolicyAsync(Vlan("Others", 30, new PolicyDevices { Serials = [Other] }));

        var tagged = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        Assert.Equal(20, PluginSwitch.VlanOf(tagged));
        Assert.Equal(VlanAttributes.TunnelTypeVlan, tagged.Find(64)!.Value.Value.Span[^1]);
        var other = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Other)))).LastAnswer!;
        Assert.Equal(30, PluginSwitch.VlanOf(other));
        var log = await LastLogRowAsync(harness);
        Assert.Equal((30, "Others"), (log.Vlan!.Value, log.VlanPolicy));
        Assert.Equal("VLAN 30 by Others", PolicyTexts.VlanBy(log.Vlan.Value, log.VlanPolicy));
    }

    [Fact]
    public void OfTwoVlanPoliciesThatHoldADeviceTheOlderGivesItsVlanAndBlockWinsOverVlan()
    {
        var device = new FakeDevice { Serial = Serial, Tags = ["PTZ"] };
        var older = Vlan("Building B", 30, new PolicyDevices { ThisServer = true });
        var newer = Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"] });
        Assert.Equal((30, "Building B"), new PolicyPlan([older, newer]).VlanFor(device));
        Assert.Equal((40, "PTZ"), new PolicyPlan([newer, older]).VlanFor(device));

        var block = new PkiPolicy { Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Serials = [Serial] } };
        var plan = new PolicyPlan([older, block]);
        Assert.Equal("Storage room", plan.RefusalFor(device)?.Name);
        Assert.Equal((null, null), new PolicyPlan([older]).VlanFor(null)); // a device OADM does not manage: no VLAN

        // ... except from an "All devices" policy, the oldest of them; an "All devices" Block refuses it.
        var everyone = Vlan("All devices", 99, PolicyDevices.Everything);
        Assert.Equal((99, "All devices"), new PolicyPlan([older, everyone]).VlanFor(null));
        Assert.Equal((30, "Building B"), new PolicyPlan([older, everyone]).VlanFor(device));
        var blockAll = new PkiPolicy { Name = "Closed", Kind = PolicyKinds.Block, Devices = PolicyDevices.Everything };
        Assert.Equal("Closed", new PolicyPlan([block, blockAll]).RefusalFor(null)?.Name);
        Assert.Null(plan.RefusalFor(null));
    }

    [Fact]
    public async Task ABlockPolicyRefusesItsDevicesWithItsNameRevokesNothingAndADeleteLetsThemIn()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.AddDevice(Other);
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var block = await harness.SavePolicyAsync(new PkiPolicy
        {
            Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { ThisServer = true, ExceptSerials = [Other.ToLowerInvariant()] },
        });

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
        Assert.Equal("Blocked by Storage room", (await LastLogRowAsync(harness)).Reason);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Other)))).Final); // excepted
        Assert.Empty(harness.Ca.RevokeCalls);

        // The change applies to the next handshake at once.
        await harness.Policies.DeleteAsync(block.Id, "admin", CancellationToken.None);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
    }

    [Fact]
    public async Task ARevokePolicyRefusesANewCertificateOfItsDevicesAndRevokesIt()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.SavePolicyAsync(new PkiPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Serials = [Serial] } });
        var fresh = harness.Ca.Camera(Serial);

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, fresh))).Final);

        Assert.Equal("Certificate revoked by Stolen", (await LastLogRowAsync(harness)).Reason);
        await Eventually.TrueAsync(() => harness.Ca.IsRevoked(TestAuthority.SerialOf(fresh)), "the new certificate revoked in PKI");
        Assert.Equal("admin", harness.Ca.RevokeCalls.Last().User);
    }

    [Fact]
    public async Task WithoutPkisPoliciesNothingPasses()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);

        harness.Ctx.FakeServices.Withdraw<PolicyStore>(); // PKI stopped

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
        Assert.Equal(CertificateRefusals.NoCa, (await LastLogRowAsync(harness)).Reason);
        Assert.Equal(0, harness.Service.GetState(hasAllDevices: true).PoliciesApply);
    }

    [Fact]
    public async Task PoliciesThatCannotBeReadLetNothingPass()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial), start: false);
        await harness.PkiSettings.SetAsync(PolicyStore.SettingsKey, "{\"policies\":7}", CancellationToken.None);
        await harness.Policies.LoadAsync(CancellationToken.None);
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
        Assert.Equal(CertificateRefusals.PoliciesUnreadable, (await LastLogRowAsync(harness)).Reason);
        Assert.Empty(harness.Policies.All); // the 802.1X server's conversion wrote nothing over it
        Assert.Equal("{\"policies\":7}", await harness.PkiSettings.GetAsync(PolicyStore.SettingsKey, CancellationToken.None));
    }

    [Fact]
    public async Task ReauthenticationOfTheServerPolicyIsSentAsSessionTimeoutWithTerminationActionRadiusRequest()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var switchDecides = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        Assert.Null(switchDecides.Find(RadiusAttributeType.SessionTimeout));
        Assert.Null(switchDecides.Find(RadiusAttributeType.TerminationAction));

        await harness.SetServerPolicyAsync(p => p with { ReauthMinutes = 60 });

        var accept = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        Assert.Equal(RadiusCode.AccessAccept, accept.Code);
        Assert.Equal(3600u, BinaryPrimitives.ReadUInt32BigEndian(accept.Find(RadiusAttributeType.SessionTimeout)!.Value.Value.Span)); // RFC 3580 3.17
        Assert.Equal(ReauthenticationAttributes.RadiusRequest, BinaryPrimitives.ReadUInt32BigEndian(accept.Find(RadiusAttributeType.TerminationAction)!.Value.Value.Span)); // 3.19
    }

    [Fact]
    public async Task TheDefaultsOfANewServerAreThere()
    {
        await using var harness = await PluginHarness.CreateAsync();

        var server = Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Dot1xServer));
        Assert.Equal(("802.1X server", "1.2", 1020, (int?)null), (server.Name, server.MinimumTls, server.LargestEapPacket!.Value, server.ReauthMinutes));
        Assert.Equal([("OADM CA", CertificateAuthorityInfo.OadmId, true), ("Axis device ID", CertificateAuthorityInfo.AxisDeviceIdId, true)],
            harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Select(p => (p.Name, p.CaId!, p.OnlyManaged!.Value)));
        Assert.Empty(harness.Policies.PoliciesOf(PolicyKinds.Vlan)); // no default VLAN
        Assert.Equal(3, harness.Service.GetState(hasAllDevices: true).PoliciesApply);
    }

    [Fact]
    public void TheTagsOfVlanPoliciesMatchOnlyWhileTheHostProtectsThemAndRevokeNeverMatchesByTag()
    {
        var tagged = new FakeDevice { Serial = Other, Tags = ["Lost"] };
        var vlan = Vlan("Lost VLAN", 20, new PolicyDevices { Tags = ["Lost"] });
        var block = new PkiPolicy { Name = "Lost", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Tags = ["Lost"] } };
        var revoke = new PkiPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Tags = ["Lost"], Serials = [Serial] } };

        Assert.Equal((20, "Lost VLAN"), new PolicyPlan([vlan]).VlanFor(tagged));
        Assert.Equal((null, null), new PolicyPlan([vlan], tagsProtected: false).VlanFor(tagged)); // fail closed: a group user could tag a device into a VLAN
        Assert.Equal("Lost", new PolicyPlan([block], tagsProtected: false).RefusalFor(tagged)?.Name);
        Assert.Null(new PolicyPlan([revoke]).RefusalFor(tagged));
        Assert.Equal("Stolen", new PolicyPlan([revoke]).RefusalFor(new FakeDevice { Serial = Serial })?.Name);
    }
}
