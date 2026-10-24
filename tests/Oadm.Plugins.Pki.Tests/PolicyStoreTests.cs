using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// PKI's policies (docs/spec/pki.md "Policies"): the store's checks (names, kinds, CAs, devices, sites), overlaps against
/// the devices of now and later ones with their warning, the expiry days per device, the protected tags, the defaults and
/// PKI's own conversion, the permissions of the methods, Revoke on save and every minute, Used by of the CAs.
/// </summary>
public sealed class PolicyStoreTests
{
    private static readonly IReadOnlyList<PolicyCipherSuite> Suites = PkiService.CipherSuites;

    private static PolicyStore Store(List<IDeviceInfo> devices, FakeSites? sites = null, FakeProtectedTags? tags = null, IReadOnlyList<CertificateAuthorityInfo>? authorities = null,
        bool unprotected = false) =>
        new(new FakeSettings(), _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. devices]), () => sites?.List ?? [],
            () => authorities ?? [new CertificateAuthorityInfo(CertificateAuthorityInfo.OadmId, "OADM CA", CertificateAuthorityKind.Oadm, [], DateTime.UtcNow.AddYears(1), true),
                new CertificateAuthorityInfo(CertificateAuthorityInfo.AxisDeviceIdId, "Axis device ID", CertificateAuthorityKind.AxisDeviceId, [], DateTime.UtcNow.AddYears(9), false)],
            unprotected ? null : tags ?? new FakeProtectedTags());

    private static PkiPolicy Vlan(string name, int vlan, PolicyDevices devices) => new() { Name = name, Kind = PolicyKinds.Vlan, Vlan = vlan, Devices = devices };

    private static PkiPolicy Expiry(string name, int days, PolicyDevices devices) => new() { Name = name, Kind = PolicyKinds.ExpiryWarning, Days = days, Devices = devices };

    private static async Task<PkiPolicy> SaveAsync(PolicyStore store, PkiPolicy policy)
    {
        var result = await store.SaveAsync(policy, "admin", Suites, CancellationToken.None);
        Assert.Null(result.Error);
        return result.Saved!;
    }

    private static async Task<PolicyFieldError?> RefusedAsync(PolicyStore store, PkiPolicy policy) => (await store.SaveAsync(policy, "admin", Suites, CancellationToken.None)).Error;

    [Fact]
    public async Task EveryPolicyIsCheckedOnItsOwnAndAgainstTheOthers()
    {
        var sites = new FakeSites();
        var site = sites.Add("Building B");
        using var store = Store([], sites);
        var lobby = await SaveAsync(store, Vlan("Lobby", 10, new PolicyDevices { SiteIds = [site.Id] }));

        Assert.Equal(new PolicyFieldError(PolicyFields.Name, PolicyTexts.EnterName), await RefusedAsync(store, Vlan(" ", 10, new PolicyDevices { ThisServer = true })));
        Assert.Equal(new PolicyFieldError(PolicyFields.Name, PolicyTexts.NameTooLong), await RefusedAsync(store, Vlan(new string('n', 65), 10, new PolicyDevices { ThisServer = true })));
        Assert.Equal(new PolicyFieldError(PolicyFields.Name, PolicyTexts.NameTaken(PolicyKinds.Vlan)), await RefusedAsync(store, Vlan("lobby", 11, new PolicyDevices { ThisServer = true })));
        Assert.Equal("Another VLAN policy has this name.", PolicyTexts.NameTaken(PolicyKinds.Vlan));
        Assert.Null(await RefusedAsync(store, Expiry("Lobby", 30, new PolicyDevices { ThisServer = true }))); // the same name in another kind
        Assert.Equal(new PolicyFieldError(PolicyFields.Vlan, PolicyTexts.VlanRange), await RefusedAsync(store, Vlan("Big", 4095, new PolicyDevices { ThisServer = true })));
        Assert.Equal(new PolicyFieldError(PolicyFields.Days, PolicyTexts.DaysRange), await RefusedAsync(store, Expiry("Long", 366, new PolicyDevices { ThisServer = true })));
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, PolicyTexts.ChooseDevices), await RefusedAsync(store, Vlan("None", 12, new PolicyDevices())));
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, PolicyTexts.RevokeNoTags),
            await RefusedAsync(store, new PkiPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Tags = ["Lost"] } }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, PolicyTexts.SiteGone), await RefusedAsync(store, Vlan("Gone", 13, new PolicyDevices { SiteIds = [Guid.NewGuid()] })));
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.ChooseKind), await RefusedAsync(store, new PkiPolicy { Name = "What", Kind = "nope" }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.KindKept), await RefusedAsync(store, lobby with { Kind = PolicyKinds.Block }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Name, PolicyTexts.PolicyGone), await RefusedAsync(store, lobby with { Id = Guid.NewGuid() }));

        // Allow CA: a CA of PKI's list, once.
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.ChooseCa), await RefusedAsync(store, new PkiPolicy { Name = "No CA", Kind = PolicyKinds.AllowCa }));
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.UnknownCa), await RefusedAsync(store, new PkiPolicy { Name = "Gone", Kind = PolicyKinds.AllowCa, CaId = "imported:gone" }));
        await SaveAsync(store, new PkiPolicy { Name = "OADM CA", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.OadmId });
        Assert.Equal(new PolicyFieldError(PolicyFields.Ca, PolicyTexts.CaTaken), await RefusedAsync(store, new PkiPolicy { Name = "Again", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.OadmId }));
        Assert.True((await SaveAsync(store, new PkiPolicy { Name = "Factory", Kind = PolicyKinds.AllowCa, CaId = CertificateAuthorityInfo.AxisDeviceIdId })).OnlyManaged); // on unless off

        // The 802.1X server policy: one only, its own checks.
        var server = Radius.Server.RadiusPolicies.DefaultServerPolicy();
        await SaveAsync(store, server);
        Assert.Equal(new PolicyFieldError(PolicyFields.Kind, PolicyTexts.OneOnly(PolicyKinds.Dot1xServer)), await RefusedAsync(store, server with { Name = "Second" }));
        var saved = store.ServerPolicy!;
        Assert.Equal(new PolicyFieldError(PolicyFields.LargestEapPacket, PolicyRules.EapPacketRange), await RefusedAsync(store, saved with { LargestEapPacket = 1401 }));
        Assert.Equal(new PolicyFieldError(PolicyFields.ReauthMinutes, PolicyTexts.ReauthRange), await RefusedAsync(store, saved with { ReauthMinutes = 5 }));
        Assert.Equal(new PolicyFieldError(PolicyFields.MinimumTls, PolicyRules.ChooseTls), await RefusedAsync(store, saved with { MinimumTls = "2.0" }));
        Assert.Equal(new PolicyFieldError(PolicyFields.CipherSuites, PolicyRules.CheckCipherSuite), await RefusedAsync(store, saved with { CipherSuites = [] }));
        Assert.Equal(60, (await SaveAsync(store, saved with { ReauthMinutes = 60 })).ReauthMinutes);
    }

    [Fact]
    public async Task ADeviceInTwoPoliciesOfAKindIsRefusedAgainstTheDevicesOfNowAndOtherKindsMayShareIt()
    {
        var devices = new List<IDeviceInfo>
        {
            new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] },
            new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49", Tags = ["PTZ"] },
            new FakeDevice { Serial = "ACCC8E000003", Address = "10.0.0.50" },
        };
        using var store = Store(devices);
        await SaveAsync(store, Vlan("Building B", 30, new PolicyDevices { ThisServer = true, ExceptSerials = ["ACCC8E000002"] }));

        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, "10.0.0.48 is in PTZ and Building B."),
            await RefusedAsync(store, Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"] })));
        Assert.Equal("10.0.0.48 and 1 other device are in PTZ and Building B.", (await RefusedAsync(store, Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"], Serials = ["ACCC8E000003"] })))!.Message);
        Assert.Null(await RefusedAsync(store, Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"], ExceptSerials = ["ACCC8E000001"] })));
        Assert.Null(await RefusedAsync(store, new PkiPolicy { Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { ThisServer = true } })); // Block over VLAN
        Assert.Null(await RefusedAsync(store, Expiry("All devices", 30, new PolicyDevices { ThisServer = true })));
    }

    [Fact]
    public async Task AllDevicesHoldsEveryDeviceAndOverlapsEveryOtherPolicyOfItsKindButNeverRevokes()
    {
        var sites = new FakeSites();
        var devices = new List<IDeviceInfo> { new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48" } };
        using var store = Store(devices, sites);
        var all = await SaveAsync(store, Vlan("Everyone", 99, new PolicyDevices { AllDevices = true, ThisServer = true, Serials = ["ACCC8E000001"] }));
        Assert.Equal(PolicyDevices.Everything, all.Devices); // "All devices" alone
        Assert.True(PolicyMatcher.Of(all).Holds(new FakeDevice { SiteId = sites.Add("Building C").Id })); // a site bound later
        Assert.Equal(PolicyTexts.AllDevices, PolicyTexts.DevicesText(all.Devices, new Dictionary<Guid, string>()));

        // Refused on save as any overlap, also without a shared device of now.
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, "Everyone includes every device, also those of PTZ."),
            await RefusedAsync(store, Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"] })));
        await SaveAsync(store, Expiry("PTZ", 60, new PolicyDevices { Tags = ["PTZ"] })); // another kind
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, "All devices includes every device, also those of PTZ."),
            await RefusedAsync(store, Expiry("All devices", 30, PolicyDevices.Everything)));
        Assert.Null(await RefusedAsync(store, new PkiPolicy { Name = "Lobby", Kind = PolicyKinds.Block, Devices = PolicyDevices.Everything })); // Block may hold what VLAN holds
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, PolicyTexts.RevokeNoAll),
            await RefusedAsync(store, new PkiPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = PolicyDevices.Everything }));
        Assert.False(PolicyKinds.AllowsAllDevices(PolicyKinds.Revoke));
    }

    [Fact]
    public async Task AnOverlapATagMadeLaterWarnsInBothRowsAndTheSaferOneApplies()
    {
        var device = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48" };
        var devices = new List<IDeviceInfo> { device };
        using var store = Store(devices);
        var older = await SaveAsync(store, Vlan("Building B", 30, new PolicyDevices { ThisServer = true }));
        var newer = await SaveAsync(store, Vlan("PTZ", 40, new PolicyDevices { Tags = ["PTZ"] }));
        await SaveAsync(store, Expiry("All devices", 30, new PolicyDevices { ThisServer = true }));
        await SaveAsync(store, Expiry("PTZ cameras", 60, new PolicyDevices { Tags = ["PTZ"] }));
        Assert.All(store.Rows(devices), r => Assert.Null(r.Warning));

        devices[0] = device with { Tags = ["PTZ"] }; // tagged later

        var rows = store.Rows(devices);
        var warning = "10.0.0.48 is in Building B and PTZ. Building B applies.";
        Assert.Equal(warning, rows.Single(r => r.Policy.Id == older.Id).Warning);
        Assert.Equal(warning, rows.Single(r => r.Policy.Id == newer.Id).Warning);
        Assert.Equal("10.0.0.48 is in All devices and PTZ cameras. PTZ cameras applies.", rows.Single(r => r.Policy.Name == "All devices").Warning); // the longer warning
        Assert.Equal(60, store.ExpiryDaysFor(devices[0]));

        // A later edit of something else is never refused for the overlap stored before.
        Assert.Null(await RefusedAsync(store, newer with { Vlan = 41 }));
    }

    [Fact]
    public async Task TheDaysOfAnExpiryWarningAreTheLongestOfThePoliciesThatHoldTheDeviceElse30()
    {
        var site = Guid.NewGuid();
        var sites = new FakeSites();
        var b = sites.Add("Building B");
        using var store = Store([], sites);
        Assert.Equal((30, 30), (store.ExpiryDaysFor(new FakeDevice()), store.DefaultExpiryDays));

        await SaveAsync(store, Expiry("This server", 45, new PolicyDevices { ThisServer = true }));
        await SaveAsync(store, Expiry("Building B", 90, new PolicyDevices { SiteIds = [b.Id] }));

        Assert.Equal(45, store.ExpiryDaysFor(new FakeDevice()));
        Assert.Equal(90, store.ExpiryDaysFor(new FakeDevice { SiteId = b.Id }));
        Assert.Equal(30, store.ExpiryDaysFor(new FakeDevice { SiteId = site })); // a site no policy names: the default
        Assert.Equal(90, store.DefaultExpiryDays); // a new Compliance check: the longest
    }

    [Fact]
    public async Task TheTagsOfThePoliciesAreProtectedExceptRevokeAndAWithoutProtectionVlanTagMatchesNothing()
    {
        var tags = new FakeProtectedTags();
        using var store = Store([], tags: tags);
        var vlan = await SaveAsync(store, Vlan("Twenty", 20, new PolicyDevices { Tags = ["Vlan 20"] }));
        await SaveAsync(store, new PkiPolicy { Name = "Lobby", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Tags = ["Vlan 21"] } });

        Assert.Equal(["Vlan 20", "Vlan 21"], tags.Names.Order(StringComparer.Ordinal));
        Assert.Equal(PolicyTexts.TagRefusal("VLAN 20"), tags.RefusalFor("VLAN 20")); // case-insensitive
        Assert.Equal("The tag Vlan 20 is used by a PKI policy. Only users with all devices can add or remove it. Nothing was changed.", tags.RefusalFor("Vlan 20"));
        Assert.Equal(PolicyTexts.TagChangeRefusal("Vlan 20"), tags.ChangeRefusalFor("Vlan 20"));
        await store.DeleteAsync(vlan.Id, "admin", CancellationToken.None);
        Assert.Equal(["Vlan 21"], tags.Names); // a deleted policy frees its tag

        using var unprotected = Store([], unprotected: true);
        var tagged = new FakeDevice { Tags = ["Vlan 20"] };
        Assert.False(unprotected.TagsProtected);
        Assert.False(PolicyMatcher.Of(Vlan("Twenty", 20, new PolicyDevices { Tags = ["Vlan 20"] }), tagsProtected: false).Holds(tagged));
        Assert.True(PolicyMatcher.Of(new PkiPolicy { Kind = PolicyKinds.Block, Devices = new PolicyDevices { Tags = ["Vlan 20"] } }, tagsProtected: false).Holds(tagged));
    }

    [Fact]
    public async Task PkisOwnSettingsBecomeTheExpiryWarningAllDevicesOnceWithItsDays()
    {
        var settings = new InMemoryPluginSettingsProvider();
        await settings.GetSettings(PkiPluginInfo.PluginId).SetAsync(PkiStore.ConfigKey, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pki-config-4.1.json")), CancellationToken.None);
        var sites = new FakeSites();
        var site = sites.Add("Building B");

        await using (var pki = await PkiHarness.StartAsync(settings, sites: sites))
        {
            var expiry = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.ExpiryWarning));
            Assert.Equal((PolicyLimits.AllDevicesName, 45), (expiry.Name, expiry.Days!.Value)); // OADM 4.1's "Warn before expiry"
            Assert.True(expiry.Devices!.AllDevices);
            Assert.Equal(45, pki.Policies.ExpiryDaysFor(new FakeDevice { SiteId = site.Id }));
            Assert.Equal(45, pki.Policies.ExpiryDaysFor(new FakeDevice { SiteId = sites.Add("Building C").Id })); // a site bound later too
            Assert.Equal(730, pki.Service.Config.DeviceCertValidityDays); // the other settings stay where they are (stage 4)
            await pki.Policies.SaveAsync(expiry with { Days = 20 }, "admin", Suites, CancellationToken.None);
        }

        await using var again = await PkiHarness.StartAsync(settings, sites: sites);
        Assert.Equal(20, Assert.Single(again.Policies.PoliciesOf(PolicyKinds.ExpiryWarning)).Days); // converted once: the change stays
        Assert.Contains(PkiPluginInfo.PluginId, again.Policies.Converted);
    }

    [Fact]
    public async Task ANewServerStartsWithTheExpiryWarningAllDevicesOf30Days()
    {
        await using var pki = await PkiHarness.StartAsync();
        var expiry = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.ExpiryWarning));
        Assert.Equal(("All devices", 30), (expiry.Name, expiry.Days!.Value));
        Assert.Same(pki.Policies, pki.Core.FakeServices.Find<PolicyStore>()); // offered for the 802.1X server inside PKI
    }

    [Fact]
    public async Task ThePoliciesAreChangedOnlyWithManageAndAllDevicesReadOnlyWithoutAndNoneForAGroupUser()
    {
        var plugin = new PkiPlugin();
        Assert.Equal(UserRole.Operator, plugin.RequiredRole(PolicyMethods.GetPolicies));
        foreach (var method in new[] { PolicyMethods.SavePolicy, PolicyMethods.DeletePolicy, PolicyMethods.CountRevocations })
        {
            Assert.Equal(UserRole.Admin, plugin.RequiredRole(method)); // "Manage the certificate authority"
            Assert.True(plugin.RequiresAllDevices(method));
            Assert.Null(plugin.RequiredPermission(method));
        }

        Assert.True(plugin.IsAudited(PolicyMethods.SavePolicy));
        Assert.True(plugin.IsAudited(PolicyMethods.DeletePolicy));
        Assert.False(plugin.IsAudited(PolicyMethods.CountRevocations)); // only reads
        Assert.False(plugin.RequiresAllDevices(PolicyMethods.GetPolicies));

        var device = new FakeDevice { Serial = "ACCC8E000001", Tags = ["Lobby"] };
        await using var pki = await PkiHarness.StartAsync(devices: [device]);
        var ctx = new FakeCoreContext();
        ctx.Add(device);
        var all = await ListAsync(pki, ctx.CallAsAllDevices());
        Assert.True(all.MayManage);
        Assert.NotEmpty(all.Rows);
        Assert.Equal(["Lobby"], all.Tags);
        Assert.NotEmpty(all.CipherSuites);
        Assert.Contains(all.Cas, c => c.Id == CertificateAuthorityInfo.AxisDeviceIdId);

        var readOnly = new LimitedCall(ctx.CallAsAllDevices());
        Assert.False((await ListAsync(pki, readOnly)).MayManage);
        Assert.NotEmpty((await ListAsync(pki, readOnly)).Rows);

        var group = await ListAsync(pki, ctx.CallAsGroup([device.Id]));
        Assert.Empty(group.Rows); // the policies are server-wide
        Assert.Empty(group.Tags);
        Assert.False(group.MayManage);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() =>
            pki.Plugin.InvokeAsync(ctx.CallAsGroup([device.Id]), PolicyMethods.SavePolicy, PkiJson.Serialize(Vlan("x", 1, new PolicyDevices { ThisServer = true })), CancellationToken.None));
    }

    private static async Task<PolicyList> ListAsync(PkiHarness pki, ICallContext call) =>
        PkiJson.Deserialize<PolicyList>(await pki.Plugin.InvokeAsync(call, PolicyMethods.GetPolicies, null, CancellationToken.None));

    [Fact]
    public async Task ARevokePolicyRevokesTheIeee8021xCertificatesOfItsDevicesOnSaveAndNewOnesEveryMinute()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var device = new FakeDevice { Serial = "ACCC8E000001" };
        var other = new FakeDevice { Serial = "ACCC8E000002" };
        await using var pki = await PkiHarness.StartAsync(time: time, devices: [device, other]);
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var current = await RevocationTests.SignAsync(authority, device.Id, device.Serial, DeviceCertificateUse.Client);
        var https = await RevocationTests.SignAsync(authority, device.Id, device.Serial);
        var others = await RevocationTests.SignAsync(authority, other.Id, other.Serial, DeviceCertificateUse.Client);
        var policy = new PkiPolicy { Name = "Stolen", Kind = PolicyKinds.Revoke, Devices = new PolicyDevices { Serials = [device.Serial] } };

        Assert.Equal(1, (await pki.InvokeAsync<PolicyRevocationCount>(PolicyMethods.CountRevocations, policy.Devices)).Count);
        var reply = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy);

        Assert.Null(reply.Error);
        Assert.Equal("OADM", reply.Saved!.SavedBy);
        var revocation = pki.Service.Issued.FindRevocation(current.SerialNumber);
        Assert.Equal((RevocationReason.Other, "OADM"), (revocation!.Reason, revocation.UserName));
        Assert.False(pki.Service.Issued.IsRevoked(https.SerialNumber)); // another use: kept
        Assert.False(pki.Service.Issued.IsRevoked(others.SerialNumber));
        Assert.Equal(0, (await pki.InvokeAsync<PolicyRevocationCount>(PolicyMethods.CountRevocations, policy.Devices)).Count);

        // A new certificate of the device (EST, a task): revoked by the minute round.
        var fresh = await RevocationTests.SignAsync(authority, device.Id, device.Serial, DeviceCertificateUse.Client);
        time.Advance(TimeSpan.FromMinutes(1));
        await Eventually.TrueAsync(() => pki.Service.Issued.IsRevoked(fresh.SerialNumber), "the new certificate revoked");
    }

    [Fact]
    public async Task TheAllowCaPoliciesAreTheUsedByOfTheirCaAndAnImportedCaTheyNameIsNotRemoved()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var root = TestCa.Root("Contoso Root");
        var imported = await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(root.RawData), "contoso.cer"));
        Assert.True(imported.Ok);
        var id = imported.State!.Authorities.Single(a => a.Kind == CertificateAuthorityKind.Imported).Id;
        var allow = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Contoso", Kind = PolicyKinds.AllowCa, CaId = id, OnlyManaged = false });
        Assert.Null(allow.Error);
        Assert.Contains("Contoso Root", allow.List!.Rows.Single(r => r.Policy.Name == "Contoso").Summary, StringComparison.Ordinal);

        var state = await pki.StateAsync();
        Assert.Equal(["802.1X: Contoso"], state.Authorities.Single(a => a.Id == id).UsedBy);

        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id));
        Assert.Equal("Used by the policy Contoso. Change or delete the policy first.", refused.Error);
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.DeletePolicy, new PolicyRef(allow.Saved!.Id));
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.RemoveAuthority, new RemoveAuthorityRequest(id))).Ok);
    }

    [Fact]
    public async Task AChangeTellsThePagesAndTheHostTheNewExpiryDays()
    {
        var device = new FakeDevice { Serial = "ACCC8E000001" };
        await using var pki = await PkiHarness.StartAsync(devices: [device]);
        Assert.Equal(30, pki.Anchors.ExpiryWarningDays(device));
        var changes = 0;
        pki.Anchors.ExpiryWarningChanged += (_, _) => changes++;
        using var events = new CancellationTokenSource();
        var topics = new List<string>();
        var watching = Task.Run(async () =>
        {
            await foreach (var e in pki.Hub.WatchAsync(PkiPluginInfo.PluginId, events.Token))
            {
                lock (topics)
                {
                    topics.Add(e.Topic);
                }
            }
        });
        await Eventually.TrueAsync(() => pki.Hub.WatcherCount(PkiPluginInfo.PluginId) > 0, "the page watches");

        var expiry = pki.Policies.PoliciesOf(PolicyKinds.ExpiryWarning)[0];
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, expiry with { Days = 60 });
        await Eventually.TrueAsync(() => { lock (topics) { return topics.Contains(PolicyMethods.Topic); } }, "the policies event");
        Assert.Equal(60, pki.Anchors.ExpiryWarningDays(device)); // the device grid's chip
        Assert.Equal(60, pki.Anchors.ExpiryDefaultDays); // a new Compliance check
        Assert.Equal(1, changes);

        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, Vlan("Ten", 10, new PolicyDevices { ThisServer = true }));
        Assert.Equal(1, changes); // another kind: the host sends no device row again
        await events.CancelAsync();
        await watching.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>A user with all devices whose role lacks PKI's "Manage the certificate authority".</summary>
    private sealed class LimitedCall(ICallContext inner) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public IDeviceRepository Devices => inner.Devices;

        public Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

        public Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

        public IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => false;

        public bool Contains(IDeviceInfo device) => inner.Contains(device);
    }
}
