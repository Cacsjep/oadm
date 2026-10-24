using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The policies (docs/spec/radius-server.md "Policies"): VLAN policies in order with the default VLAN, block and revoke
/// policies, the PKI permission of revoke policies, the count before the save and the VLAN rules of OADM 4.1 moved in.
/// </summary>
public sealed class PolicyTests
{
    private const string Serial = "ACCC8E123456";
    private const string Other = "ACCC8E654321";

    internal static RadiusPolicy Vlan(string name, int vlan, PolicyDevices devices) => new() { Name = name, Kind = PolicyKinds.Vlan, Vlan = vlan, Devices = devices };

    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static async Task<LogRow> LastLogRowAsync(PluginHarness harness)
    {
        harness.Tick();
        return (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];
    }

    [Fact]
    public async Task VlanPoliciesAssignTheFirstMatchElseTheDefaultAndAreOffWithoutOne()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial, tags: ["PTZ"]);
            h.AddDevice(Other);
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var accept = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        Assert.Null(PluginSwitch.VlanOf(accept)); // no policy: no VLAN

        var saved = await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings
        {
            Policies = [Vlan("Cameras with PTZ", 20, new PolicyDevices { Tags = ["ptz"] }), Vlan("This server", 30, new PolicyDevices { ThisServer = true })],
            DefaultVlan = 99,
        });
        Assert.All(saved.Policies, p => Assert.NotEqual(Guid.Empty, p.Id));

        var tagged = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).LastAnswer!;
        var other = (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Other)))).LastAnswer!;
        Assert.Equal(20, PluginSwitch.VlanOf(tagged));
        Assert.Equal(30, PluginSwitch.VlanOf(other));
        Assert.Equal(VlanAttributes.TunnelTypeVlan, tagged.Find(64)!.Value.Value.Span[^1]);
        var log = await LastLogRowAsync(harness);
        Assert.Equal(30, log.Vlan);

        // The default VLAN for a device no VLAN policy holds, only while one exists.
        var plan = new PolicyPlan(saved);
        Assert.Equal(99, plan.VlanFor(new FakeDevice { Serial = "ACCC8E000009", SiteId = Guid.NewGuid() }));
        Assert.Null(new PolicyPlan(new PolicySettings { DefaultVlan = 99 }).VlanFor(new FakeDevice { Serial = Serial }));
    }

    [Fact]
    public async Task ABlockPolicyRefusesItsDevicesWithItsNameAndRevokesNothing()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.AddDevice(Other);
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings
        {
            Policies = [new RadiusPolicy { Name = "Building C", Kind = PolicyKinds.Block, Devices = new PolicyDevices { ThisServer = true, ExceptSerials = [Other.ToLowerInvariant()] } }],
        });

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
        Assert.Equal("Blocked by policy Building C", (await LastLogRowAsync(harness)).Reason);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Other)))).Final); // excepted
        Assert.Empty(harness.Ca.RevokeCalls);

        // Deleting the policy lets the device in again.
        await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings());
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
    }

    [Fact]
    public async Task ARevokePolicyNeedsThePkiPermissionAndRevokesTheCurrentCertificatesOnSave()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        var device = harness.Ctx.DeviceList[0];
        var current = harness.Ca.Camera(Serial);
        harness.Ca.List(current, device.Id);
        harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = "AB12", DeviceId = device.Id, Purpose = DeviceCertificatePurposes.Https }, [])); // another use: kept
        var policy = new RadiusPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Serials = [Serial] } };

        Assert.Equal(1, (await harness.CallAsync<RevocationCount>(RadiusMethods.CountRevocations, policy.Devices)).Count);
        var radiusOnly = new HardeningTests.LimitedCall(harness.Admin, PermissionKeys.Of(RadiusPlugin.PluginId, PermissionKeys.Manage));
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() =>
            harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings { Policies = [policy] }, radiusOnly));
        Assert.Equal(RadiusTexts.NeedsCaPermission, refused.Message);
        Assert.Empty((await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies)).Policies);
        Assert.Empty(harness.Ca.RevokeCalls);

        var saved = await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings { Policies = [policy] });

        var call = Assert.Single(harness.Ca.RevokeCalls);
        Assert.Equal([TestAuthority.SerialOf(current)], call.Serials);
        Assert.Equal("admin", call.User);
        Assert.Equal("admin", Assert.Single(saved.Policies).SavedBy);
        Assert.Equal(0, (await harness.CallAsync<RevocationCount>(RadiusMethods.CountRevocations, policy.Devices)).Count);

        // An unchanged revoke policy is saved again without the permission (another policy changed).
        await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, saved with { Policies = [.. saved.Policies, Vlan("Ten", 10, new PolicyDevices { ThisServer = true })] }, radiusOnly);
    }

    [Fact]
    public async Task ARevokePolicyRefusesANewCertificateOfItsDevicesAndRevokesIt()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, new PolicySettings
        {
            Policies = [new RadiusPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Serials = [Serial] } }],
        });
        var fresh = harness.Ca.Camera(Serial);

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, fresh))).Final);

        Assert.Equal("Certificate revoked by policy Stolen", (await LastLogRowAsync(harness)).Reason);
        await Eventually.TrueAsync(() => harness.Ca.IsRevoked(TestAuthority.SerialOf(fresh)), "the new certificate revoked in PKI");
        Assert.Equal("admin", harness.Ca.RevokeCalls.Last().User);
    }

    [Fact]
    public async Task PoliciesAreCheckedAsOnThePage()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var twice = new PolicySettings { Policies = [Vlan("Lobby", 10, new PolicyDevices { ThisServer = true }), Vlan("lobby", 11, new PolicyDevices { ThisServer = true })] };
        var noDevices = new PolicySettings { Policies = [Vlan("Lobby", 10, new PolicyDevices())] };
        var badVlan = new PolicySettings { Policies = [Vlan("Lobby", 4095, new PolicyDevices { ThisServer = true })] };
        var goneSite = new PolicySettings { Policies = [Vlan("Lobby", 10, new PolicyDevices { SiteIds = [Guid.NewGuid()] })] };

        Assert.Equal("policies[1].name", (await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, twice))).ParamName);
        Assert.Equal("policies[0].devices", (await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, noDevices))).ParamName);
        Assert.Equal("policies[0].vlan", (await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, badVlan))).ParamName);
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, goneSite));
        Assert.Equal("policies[0].devices", gone.ParamName);
        Assert.StartsWith(RadiusTexts.SiteGone, gone.Message, StringComparison.Ordinal);
        Assert.Empty((await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies)).Policies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheVlanRulesOfOadm41MoveIntoVlanPoliciesOnlyWhenTheyWereOn(bool enabled)
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var site = Guid.NewGuid();
        var old = new VlanSettings
        {
            Enabled = enabled,
            Rules = [new VlanRule(VlanRuleKinds.Tag, "PTZ", "PTZ", 20), new VlanRule(VlanRuleKinds.Site, RadiusLimits.ThisServerSiteId, "This server", 30), new VlanRule(VlanRuleKinds.Site, site.ToString(), "PTZ", 40)],
            DefaultVlan = 99,
        };
        await harness.Ctx.PluginSettings.SetAsync(RadiusStore.VlansKey, RadiusJson.Serialize(old), CancellationToken.None);

        await harness.StartAsync();

        var policies = await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies);
        Assert.Null(await harness.Ctx.PluginSettings.GetAsync(RadiusStore.VlansKey, CancellationToken.None));
        if (!enabled)
        {
            Assert.Empty(policies.Policies);
            Assert.Null(policies.DefaultVlan);
            return;
        }

        Assert.Equal(["PTZ", "This server", "PTZ (2)"], policies.Policies.Select(p => p.Name));
        Assert.Equal([20, 30, 40], policies.Policies.Select(p => p.Vlan!.Value));
        Assert.Equal(["PTZ"], policies.Policies[0].Devices.Tags);
        Assert.True(policies.Policies[1].Devices.ThisServer);
        Assert.Equal([site], policies.Policies[2].Devices.SiteIds);
        Assert.Equal(99, policies.DefaultVlan);
        Assert.Equal(["PTZ"], harness.Ctx.FakeProtectedTags.Names);
    }

    [Fact]
    public async Task OnlyTheDefaultVlanOfOadm41MovesIntoAVlanPolicyForEveryDevice()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var site = harness.Ctx.FakeSites.Add("Building B");
        var old = new VlanSettings { Enabled = true, Rules = [new VlanRule(VlanRuleKinds.Tag, " ", null, 20)], DefaultVlan = 99 };
        await harness.Ctx.PluginSettings.SetAsync(RadiusStore.VlansKey, RadiusJson.Serialize(old), CancellationToken.None);

        await harness.StartAsync();

        var policies = await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies);
        var all = Assert.Single(policies.Policies);
        Assert.Equal((RadiusTexts.AllDevicesPolicy, PolicyKinds.Vlan, 99), (all.Name, all.Kind, all.Vlan!.Value));
        Assert.True(all.Devices.ThisServer);
        Assert.Equal([site.Id], all.Devices.SiteIds);
        Assert.Equal(99, policies.DefaultVlan);
        var plan = new PolicyPlan(policies);
        Assert.Equal(99, plan.VlanFor(new FakeDevice { Serial = "ACCC8E000001" }));
        Assert.Equal(99, plan.VlanFor(new FakeDevice { Serial = "ACCC8E000002", SiteId = Guid.NewGuid() })); // a later site: the default VLAN
    }

    [Fact]
    public async Task APolicyIdTwiceIsRefusedAndStoredOnesNeverLockThePage()
    {
        var id = Guid.NewGuid();
        var twice = new PolicySettings
        {
            Policies = [Vlan("A", 20, new PolicyDevices { ThisServer = true }) with { Id = id }, Vlan("B", 30, new PolicyDevices { Tags = ["PTZ"] }) with { Id = id }],
        };
        Assert.Equal(new RadiusFieldError("policies", RadiusRules.PolicyListedTwice), RadiusRules.Validate(twice));
        Assert.Null(RadiusRules.Validate(twice with { Policies = [twice.Policies[0], twice.Policies[1] with { Id = Guid.Empty }] })); // new policies have none

        await using var harness = await PluginHarness.CreateAsync(start: false);
        await harness.Ctx.PluginSettings.SetAsync(RadiusStore.PoliciesKey, RadiusJson.Serialize(twice), CancellationToken.None);
        await harness.StartAsync();

        var loaded = await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies);
        Assert.Equal(["A", "B"], loaded.Policies.Select(p => p.Name));
        Assert.Equal(id, loaded.Policies[0].Id);
        Assert.Equal(2, loaded.Policies.Select(p => p.Id).Distinct().Count());
        var saved = await harness.CallAsync<PolicySettings>(RadiusMethods.SavePolicies, loaded with { Policies = [.. loaded.Policies, Vlan("C", 40, new PolicyDevices { Serials = ["ACCC8E000009"] })] });
        Assert.Equal(["A", "B", "C"], saved.Policies.Select(p => p.Name));
        Assert.Equal(id, saved.Policies[0].Id);
    }
}
