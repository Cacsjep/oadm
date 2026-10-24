using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The server part's rules from the first review (docs/spec/radius-server.md "Who passes", "Code (server)", "Device
/// groups"): only certificates the OADM CA issued itself, fail closed without PKI, revoking only IEEE 802.1X certificates
/// with PKI's permission, what a group user sees, requests without EAP, the device list after a miss, maintenance asked for
/// during a round, a stable server certificate, one socket per address, the log's search, Delete all and the revoked list.
/// </summary>
public sealed class HardeningTests
{
    private const string Serial = "ACCC8E123456";

    private static TestSupplicant Camera(byte[] trustedCa, TestCertificate certificate) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = trustedCa, Versions = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    private static async Task<LogRow> LastLogRowAsync(PluginHarness harness)
    {
        harness.Tick();
        return (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];
    }

    // ------------------------------------------------------------------ who passes

    [Fact]
    public async Task ACertificateOfAnotherCaUnderTheSameEnterpriseRootIsRefused()
    {
        // The OADM CA was imported as an intermediate of an enterprise root: the root anchors the chain, but only a
        // certificate the OADM CA itself issued names a managed device (a sibling CA could issue any axis-<serial>.local).
        using var root = new TestAuthority("Enterprise root");
        var oadm = new TestAuthority("OADM CA", root);
        using var sibling = new TestAuthority("Sibling CA", root);
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial), authority: oadm);
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        var passed = await harness.Switch().RunAsync(Camera(oadm.CaCertificate, oadm.Camera(Serial)));
        var refused = await harness.Switch().RunAsync(Camera(oadm.CaCertificate, sibling.Camera(Serial)));

        Assert.Equal(RadiusCode.AccessAccept, passed.Final);
        Assert.Equal(RadiusCode.AccessReject, refused.Final);
        Assert.Equal(CertificateRefusals.NotTrusted, (await LastLogRowAsync(harness)).Reason);
    }

    [Fact]
    public async Task WhenPkiStopsAnOadmCertificateNoLongerPasses()
    {
        // Fail closed: without PKI's CA there is no revocation list, so nothing passes, at once (not after the next
        // maintenance round): neither a certificate of the CA nor an Axis device ID one.
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var certificate = harness.Ca.Camera(Serial);
        var deviceId = harness.DeviceIds.Device(Serial);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, certificate))).Final);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, deviceId))).Final);

        harness.Ctx.FakeServices.Withdraw<IDeviceCertificateAuthority>();

        foreach (var presented in new[] { certificate, deviceId })
        {
            var conversation = await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, presented));
            Assert.Equal(RadiusCode.AccessReject, conversation.Final);
            Assert.StartsWith(CertificateRefusals.NoCa, (await LastLogRowAsync(harness)).Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APkiListedCertificateOfAnotherUseIsRefusedAndAnUnlistedOnePasses()
    {
        // An MQTT client certificate of the OADM CA (client authentication, axis-<serial>.local) is no network access
        // certificate; one PKI does not list (from before the list) passes as before.
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var device = harness.Ctx.DeviceList[0].Id;
        var mqtt = harness.Ca.Camera(Serial);
        var dot1x = harness.Ca.Camera(Serial);
        var unlisted = harness.Ca.Camera(Serial);
        harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = TestAuthority.SerialOf(mqtt), DeviceId = device, Purpose = DeviceCertificatePurposes.Mqtt }, mqtt.Leaf));
        harness.Ca.List(dot1x, device);

        var camera = Camera(harness.Ca.CaCertificate, mqtt);
        var refused = await harness.Switch().RunAsync(camera);
        Assert.Equal(RadiusCode.AccessReject, refused.Final);
        Assert.StartsWith(CertificateRefusals.NotDot1x, (await LastLogRowAsync(harness)).Reason, StringComparison.Ordinal);
        Assert.Equal((short)42, camera.AlertReceived); // bad certificate

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, dot1x))).Final);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, unlisted))).Final);
    }

    [Fact]
    public async Task AHandshakeNeverReadsTheCasAgainAndAChangedCaIsTrustedAtOnce()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, harness.Ca.Camera(Serial)))).Final);
        var reads = harness.Ca.PreviousReads;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, harness.Ca.Camera(Serial)))).Final);
        }

        Assert.Equal(reads, harness.Ca.PreviousReads); // no hashing of the CAs per handshake

        // The CA changed (a previous CA came with a replace): the next handshake builds the set again, without waiting for
        // the minute round.
        using var previous = new TestAuthority("Previous OADM CA");
        var ofPrevious = previous.Camera(Serial);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, ofPrevious))).Final);
        harness.Ca.Previous.Add([previous.CaCertificate]);
        harness.Ca.RaiseChanged();
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, ofPrevious))).Final);
    }

    // ------------------------------------------------------------------ revoke

    [Fact]
    public async Task RevokeNeedsTheCaPermissionOfPkiToo()
    {
        await using var harness = await PluginHarness.CreateAsync();
        harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = "7DFD05", Purpose = DeviceCertificatePurposes.Dot1x }, []));
        var radiusOnly = new LimitedCall(harness.Admin, PermissionKeys.Of(RadiusPlugin.PluginId, PermissionKeys.Manage));
        var both = new LimitedCall(harness.Admin, PermissionKeys.Of(RadiusPlugin.PluginId, PermissionKeys.Manage), PermissionKeys.Of("oadm.pki", PermissionKeys.Manage));

        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() =>
            harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest(["7DFD05"], RevokeReasons.Other), radiusOnly));

        Assert.Equal(RadiusTexts.NeedsCaPermission, refused.Message);
        Assert.Empty(harness.Ca.RevokeCalls);
        Assert.Equal(1, (await harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest(["7DFD05"], RevokeReasons.Other), both)).Revoked);
    }

    [Fact]
    public async Task OnlyIeee8021XCertificatesAreRevokedHere()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = "AA01", Purpose = DeviceCertificatePurposes.Dot1x }, []));
        harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = "BB02", Purpose = DeviceCertificatePurposes.Https }, []));

        // Another use, or a serial neither PKI nor this server's log knows: refused as a whole, nothing revoked.
        foreach (var serials in new[] { new[] { "AA01", "BB02" }, ["CC03"] })
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest(serials, RevokeReasons.KeyCompromised)));
            Assert.Equal("serials", refused.ParamName);
            Assert.StartsWith(RadiusTexts.OnlyDot1x, refused.Message, StringComparison.Ordinal);
        }

        Assert.Empty(harness.Ca.RevokeCalls);

        // A client certificate the 802.1X server saw (in its log) is an IEEE 802.1X certificate, also when PKI's list has
        // no entry for it (a certificate from before the list).
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var seen = harness.Ca.Camera(Serial);
        await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, seen));
        harness.Tick();
        var reply = await harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest(["AA01", TestAuthority.SerialOf(seen)], RevokeReasons.KeyCompromised));
        Assert.Equal(2, reply.Revoked);
    }

    [Fact]
    public async Task AnAxisDeviceIdSerialFromTheLogIsNeverRevoked()
    {
        // Revoking the factory certificate's serial would lock the camera out of 802.1X and EST for good: a serial only the
        // log knows counts only when the logged certificate was issued by an OADM CA (also a previous one).
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var factory = harness.DeviceIds.Device(Serial);
        using var previous = new TestAuthority("Previous OADM CA");
        harness.Ca.Previous.Add([previous.CaCertificate]);
        harness.Ca.RaiseChanged();
        var ofPrevious = previous.Camera(Serial);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, factory))).Final);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, ofPrevious))).Final);
        harness.Tick();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest([TestAuthority.SerialOf(factory)], RevokeReasons.DeviceReplaced)));
        Assert.StartsWith(RadiusTexts.OnlyDot1x, refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Ca.RevokeCalls);

        Assert.Equal(1, (await harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest([TestAuthority.SerialOf(ofPrevious)], RevokeReasons.DeviceReplaced))).Revoked);
    }

    [Fact]
    public async Task ManySerialsAreCheckedInOnePassWithoutSearchingPkiPerSerial()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var serials = Enumerable.Range(1, 1200).Select(i => i.ToString("X6", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        foreach (var serial in serials)
        {
            harness.Ca.Issued.Add((new IssuedCertificateInfo { SerialNumber = serial, Purpose = DeviceCertificatePurposes.Dot1x }, []));
        }

        var reply = await harness.CallAsync<RevokeReply>(RadiusMethods.Revoke, new RevokeRequest(serials, RevokeReasons.DeviceRemoved));

        Assert.Equal(1200, reply.Revoked);
        Assert.Empty(harness.Ca.IssuedQueries); // the uses come from PKI's list in memory, never one search per serial
    }

    [Fact]
    public async Task TheRevokedListComesInPagesWithTheSearch()
    {
        await using var harness = await PluginHarness.CreateAsync();
        for (var i = 1; i <= 1200; i++)
        {
            harness.Ca.Revoke(i.ToString("X6", System.Globalization.CultureInfo.InvariantCulture));
        }

        var first = await harness.CallAsync<RevokedPage>(RadiusMethods.ListRevoked);
        var second = await harness.CallAsync<RevokedPage>(RadiusMethods.ListRevoked, new RevokedQuery(null, 500, 500));
        var found = await harness.CallAsync<RevokedPage>(RadiusMethods.ListRevoked, new RevokedQuery("00:04:b0")); // 0004B0 = 1200

        Assert.Equal(500, first.Rows.Count);
        Assert.Equal(1200, first.Total);
        Assert.Equal(500, second.Rows.Count);
        Assert.NotEqual(first.Rows[0].SerialNumber, second.Rows[0].SerialNumber);
        Assert.Equal("4B0", Assert.Single(found.Rows).SerialNumber);
        Assert.Equal(1, found.Total);
        Assert.Equal(200, (await harness.CallAsync<RevokedPage>(RadiusMethods.ListRevoked, new RevokedQuery(null, 1000, 999))).Rows.Count);
    }

    // ------------------------------------------------------------------ device groups

    [Fact]
    public async Task AGroupUserGetsNoServerWideDataAndOnlyTheSwitchesOfItsRoutes()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var device = harness.AddDevice(Serial);
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        harness.Listener.Deliver(new byte[30], new IPEndPoint(IPAddress.Parse("192.168.1.2"), 1812)); // a sender that is no switch
        await Eventually.TrueAsync(() => harness.Service.GetState(true).UnknownRequests.Count == 1, "the unknown sender counted");
        var group = harness.Ctx.CallAsGroup([device.Id]);
        var withServer = harness.Ctx.CallAsGroup([device.Id]);
        withServer.ThisServer = true;

        var state = await harness.CallAsync<RadiusState>(RadiusMethods.GetState, null, group);

        Assert.False(state.HasAllDevices);
        Assert.Empty(state.Interfaces);
        Assert.Null(state.ServerCertificate);
        Assert.Equal(0, state.UnknownRequests.Count);
        Assert.Empty(state.Settings.CipherSuites);
        Assert.Empty(state.CipherSuitesListed);
        Assert.Empty(state.SitesNotAllowed);
        Assert.NotEmpty(state.StatusText);
        Assert.Empty((await harness.CallAsync<SwitchList>(RadiusMethods.ListSwitches, null, group)).Switches);
        Assert.Single((await harness.CallAsync<SwitchList>(RadiusMethods.ListSwitches, null, withServer)).Switches);

        // The switch rows' event goes to users with all devices only.
        harness.Ctx.PublishedEvents.Clear();
        await harness.AddSwitchAsync("Floor switch", "10.0.0.3");
        Assert.All(harness.Ctx.PublishedEvents.Where(e => e.Topic == RadiusTopics.Switches), e => Assert.Equal(PluginEventAudienceKind.AllDevices, e.Audience.Kind));
        Assert.DoesNotContain(harness.Ctx.EventsSeenBy(group), e => e.Topic == RadiusTopics.Switches);
    }

    [Fact]
    public async Task AGroupUserGetsEmptyVlanSettings()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var device = harness.AddDevice(Serial, tags: ["Lobby"]);
        await harness.RefreshDevicesAsync();
        await harness.CallAsync<VlanSettings>(RadiusMethods.SaveVlans, new VlanSettings
        {
            Enabled = true,
            Rules = [new VlanRule(VlanRuleKinds.Tag, "Vlan 20", null, 20), new VlanRule(VlanRuleKinds.Site, RadiusLimits.ThisServerSiteId, null, 30)],
            DefaultVlan = 99,
        });

        var mine = await harness.CallAsync<VlanSettings>(RadiusMethods.GetVlans, null, harness.Ctx.CallAsGroup([device.Id]));

        Assert.False(mine.Enabled);
        Assert.Empty(mine.Rules);
        Assert.Null(mine.DefaultVlan);
        Assert.Empty(mine.TagChoices); // no tag that sets a VLAN to choose
        var all = await harness.CallAsync<VlanSettings>(RadiusMethods.GetVlans);
        Assert.Equal(2, all.Rules.Count);
        Assert.Equal(["Lobby", "Vlan 20"], all.TagChoices.Select(t => t.Name));
    }

    [Fact]
    public async Task TheTagsOfTheVlanRulesAreProtectedAndATagRuleWithoutProtectionMatchesNothing()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var settings = new VlanSettings
        {
            Enabled = true,
            Rules = [new VlanRule(VlanRuleKinds.Tag, "Vlan 20", null, 20), new VlanRule(VlanRuleKinds.Tag, "Vlan 21", null, 21), new VlanRule(VlanRuleKinds.Site, RadiusLimits.ThisServerSiteId, null, 30)],
        };

        await harness.CallAsync<VlanSettings>(RadiusMethods.SaveVlans, settings);

        Assert.Equal(["Vlan 20", "Vlan 21"], harness.Ctx.FakeProtectedTags.Names.Order(StringComparer.Ordinal));
        Assert.NotNull(harness.Ctx.FakeProtectedTags.RefusalFor("VLAN 21")); // case-insensitive
        Assert.Equal(RadiusTexts.VlanTag("Vlan 20"), harness.Ctx.FakeProtectedTags.RefusalFor("Vlan 20"));
        Assert.Equal(RadiusTexts.VlanTagChange("Vlan 20"), harness.Ctx.FakeProtectedTags.ChangeRefusalFor("Vlan 20")); // never renamed or deleted
        Assert.Equal("The tag Vlan 20 sets a VLAN. Only users with all devices can add or remove it. Nothing was changed.", RadiusTexts.VlanTag("Vlan 20"));
        await harness.CallAsync<VlanSettings>(RadiusMethods.SaveVlans, settings with { Rules = [settings.Rules[2]] });
        Assert.Empty(harness.Ctx.FakeProtectedTags.Names); // a removed rule frees its tag

        // A host that cannot protect the tags: the tag rules match no device (fail closed), site rules as before.
        var tagged = new FakeDevice { Serial = Serial, Tags = ["Vlan 20"] };
        Assert.Equal(20, new VlanPlan(settings).For(tagged));
        Assert.Equal(30, new VlanPlan(settings, tagsProtected: false).For(tagged));
    }

    // ------------------------------------------------------------------ requests without EAP

    [Fact]
    public async Task AMacAddressCheckOfTheSwitchIsCountedApartAndNeverShowsWrongSecret()
    {
        await using var harness = await PluginHarness.CreateAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var request = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, RandomNumberGenerator.GetBytes(16), "a-shared-secret-of-24-ch"u8,
            [RadiusAttribute.Text(RadiusAttributeType.UserName, "accc8e123456"), RadiusAttribute.Text(RadiusAttributeType.CallingStationId, "AC-CC-8E-12-34-56")], messageAuthenticator: false)!;

        harness.Listener.Deliver(request, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 51812));

        await Eventually.TrueAsync(() => harness.Service.ListSwitches().Switches[0].MacOnlyCount == 1, "the request without EAP counted");
        var row = harness.Service.ListSwitches().Switches[0];
        Assert.NotEqual(SwitchStates.WrongSecret, row.State);
        Assert.Equal(0, row.DroppedCount);
    }

    // ------------------------------------------------------------------ device list, maintenance, server certificate, listening

    [Fact]
    public async Task AMissShortlyAfterAReadIsReadAgainWhenTheIntervalIsOver()
    {
        var ctx = new FakeCoreContext();
        var time = new FakeTimeProvider();
        using var index = new DeviceIndex(ctx.Devices, time);
        await index.RefreshAsync(onlyAfterMiss: false, CancellationToken.None);
        var missed = 0;
        index.Missed += (_, _) => Interlocked.Increment(ref missed);
        ctx.Add(new FakeDevice { Serial = Serial });

        Assert.Null(index.BySerial(Serial));
        var refresh = index.RefreshAsync(onlyAfterMiss: true, CancellationToken.None);
        Assert.False(refresh.IsCompleted); // read a moment ago: waits for the rest of the 2 s
        time.Advance(DeviceIndex.MissRefreshInterval);
        await refresh;

        Assert.NotNull(index.BySerial(Serial));
        Assert.Null(index.BySerial("ACCC8E000000"));
        Assert.Equal(2, Volatile.Read(ref missed)); // the miss after the read raised the event again (nothing stuck)
    }

    [Fact]
    public async Task ACaChangeDuringAMaintenanceRoundIsNeverSkipped()
    {
        await using var harness = await PluginHarness.CreateAsync();
        using var second = new TestAuthority("Second OADM CA");
        using var third = new TestAuthority("Third OADM CA");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.BeforeSign = () => hold.Task;

        harness.Ctx.FakeServices.Offer<IDeviceCertificateAuthority>(second);
        var round = harness.Service.MaintainAsync(CancellationToken.None); // signs with the second CA and waits there
        Assert.False(round.IsCompleted);
        harness.Ctx.FakeServices.Offer<IDeviceCertificateAuthority>(third); // the CA changes again during the round
        await harness.Service.MaintainAsync(CancellationToken.None); // the round is busy: it runs once more after it
        hold.SetResult();
        await round;

        Assert.Equal(1, second.SignCount);
        Assert.Equal(1, third.SignCount); // the round after the change made the certificate of the third CA
    }

    [Fact]
    public async Task TheServerCertificateKeepsItsNamesWhileCardsOnlyGoAway()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.Interfaces.Cards.Add(FixedInterfaces.Card("eth1", "10.0.5.1"));
            h.Interfaces.Cards.Add(FixedInterfaces.Card("eth2", "169.254.10.20")); // self-assigned: never a name
        });
        var names = harness.Service.GetState(true).ServerCertificate!.Names;
        Assert.Equal(["10.0.0.17", "10.0.5.1", "oadm-test"], names);
        Assert.Equal(1, harness.Ca.SignCount);

        harness.Interfaces.Cards.RemoveAll(c => c.Id == "eth1"); // a cable pulled
        await harness.Service.MaintainAsync(CancellationToken.None);
        harness.Interfaces.Cards.Add(FixedInterfaces.Card("eth1", "10.0.5.1")); // and back
        await harness.Service.MaintainAsync(CancellationToken.None);
        Assert.Equal(1, harness.Ca.SignCount);

        harness.Interfaces.Cards.Add(FixedInterfaces.Card("eth3", "10.0.6.1")); // a new name: a new certificate
        await harness.Service.MaintainAsync(CancellationToken.None);
        Assert.Equal(2, harness.Ca.SignCount);
        Assert.Contains("10.0.6.1", harness.Service.GetState(true).ServerCertificate!.Names);
    }

    [Fact]
    public async Task AllInterfacesListensOnEveryAddressOnItsOwnAndFollowsNewAddresses()
    {
        // One socket per address: the answer leaves from the address the switch sent its request to (RFC 2865 3; a switch
        // drops an answer from another address). A wildcard socket would answer from the address the routing table picks.
        // The loopback address stays listened (a test on the server itself); a new address is listened on within 5 s.
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.Interfaces.Cards.Add(new ServerNetworkInterface("lo", "lo", "Loopback", [IPAddress.Loopback], IsUp: true, IsLoopback: true));
        });
        Assert.Equal([IPAddress.Parse("10.0.0.17"), IPAddress.Loopback], harness.Ctx.FakeNetwork.LastUdpAddresses);
        Assert.DoesNotContain(InterfaceOptions.From(harness.Interfaces.Cards[^1]).Id, harness.Service.ListInterfaces().Select(i => i.Id)); // not a choice
        var first = harness.Listener;

        harness.Interfaces.Cards.Add(FixedInterfaces.Card("eth1", "10.0.5.1"));
        harness.Time.Advance(TimeSpan.FromSeconds(5));

        await Eventually.TrueAsync(() => harness.Ctx.FakeNetwork.LastUdpAddresses.Count == 3, "listening on the new address too");
        Assert.Equal([IPAddress.Parse("10.0.0.17"), IPAddress.Parse("10.0.5.1"), IPAddress.Loopback], harness.Ctx.FakeNetwork.LastUdpAddresses);
        await Eventually.TrueAsync(() => !ReferenceEquals(first, harness.Service.Listener), "the new listener in place");
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var conversation = await harness.Switch().RunAsync(Camera(harness.Ca.CaCertificate, harness.Ca.Camera(Serial)));
        Assert.Equal(RadiusCode.AccessAccept, conversation.Final); // the new listener answers
    }

    // ------------------------------------------------------------------ log

    [Fact]
    public void TheLogSearchFindsTheTimeAsThePageShowsIt()
    {
        var log = new RadiusLog(null, new FakeTimeProvider());
        log.Add(new LogRow { TimeUtc = new DateTime(2026, 10, 24, 12, 5, 0, DateTimeKind.Utc), SwitchName = "Core switch" }, null);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        Assert.Equal(1, log.Page("12:05", 0, 500, null).Total); // UTC as before
        Assert.Equal(0, log.Page("14:05", 0, 500, null).Total);
        Assert.Equal(1, log.Page("14:05", 0, 500, null, plusTwo).Total); // the page's local time
        Assert.Equal(1, log.Page("2026-10-24 14:05", 0, 500, null, plusTwo).Total);
        Assert.Equal(0, log.Page("16:05", 0, 500, null, plusTwo).Total);
    }

    [Fact]
    public async Task TheLogSearchTakesThePagesTimeZoneAndIgnoresAnUnknownOne()
    {
        await using var harness = await PluginHarness.CreateAsync();
        harness.Service.Log.Add(new LogRow { TimeUtc = new DateTime(2026, 10, 24, 12, 5, 0, DateTimeKind.Utc) }, null);

        Assert.Equal(1, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("12:05") { TimeZoneId = "Nowhere/Nothing" })).Total);
        Assert.Equal(1, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("12:05") { TimeZoneId = TimeZoneInfo.Utc.Id })).Total);
    }

    [Fact]
    public async Task DeleteAllDuringAFailedWriteNeverBringsTheRowsBack()
    {
        var records = new FailingRecords();
        var log = new RadiusLog(records, new FakeTimeProvider());
        await log.LoadAsync(1000, CancellationToken.None);
        log.Add(new LogRow { SwitchName = "Core switch" }, [1, 2, 3]);
        records.DuringNextWrite = () => log.Clear(); // Delete all while the write runs, and the write fails

        await Assert.ThrowsAsync<IOException>(() => log.FlushAsync(CancellationToken.None));
        await log.FlushAsync(CancellationToken.None);

        Assert.Empty(await records.ListAsync(RadiusLog.LogCollection, CancellationToken.None));
        Assert.Empty(await records.ListAsync(RadiusLog.CertificatesCollection, CancellationToken.None));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public async Task CertificatesNoRowPointsToAreDeletedAtTheStart()
    {
        var records = new MemoryPluginRecords();
        var first = new RadiusLog(records, new FakeTimeProvider());
        await first.LoadAsync(1000, CancellationToken.None);
        first.Add(new LogRow { SwitchName = "Core switch" }, [1, 2, 3]);
        await first.FlushAsync(CancellationToken.None);
        await records.SetManyAsync(RadiusLog.CertificatesCollection, [new("ORPHAN", "{\"der\":\"AQID\"}")], CancellationToken.None);

        var second = new RadiusLog(records, new FakeTimeProvider());
        await second.LoadAsync(1000, CancellationToken.None);
        await second.FlushAsync(CancellationToken.None);

        var kept = await records.ListAsync(RadiusLog.CertificatesCollection, CancellationToken.None);
        Assert.Single(kept);
        Assert.DoesNotContain("ORPHAN", kept.Keys);
        Assert.Single(await records.ListAsync(RadiusLog.LogCollection, CancellationToken.None));
    }

    // ------------------------------------------------------------------ Advanced

    [Fact]
    public void OnlyRsaSuitesCannotBeSavedWithTheEcdsaServerCertificate()
    {
        var listed = RadiusService.CipherSuites;
        var rsa = listed.Single(s => s.Id == CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256);
        var ecdsa = listed.Single(s => s.Id == CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256);

        Assert.False(rsa.Usable);
        Assert.True(ecdsa.Usable);
        Assert.All(listed.Where(s => s.Tls13), s => Assert.True(s.Usable));
        Assert.Equal(new RadiusFieldError("cipherSuites", RadiusRules.CheckCipherSuite),
            RadiusRules.Validate(new RadiusSettings { CipherSuites = [rsa.Id] }, listed));
        Assert.Null(RadiusRules.Validate(new RadiusSettings { CipherSuites = [rsa.Id, ecdsa.Id] }, listed));
    }

    /// <summary>A user's call whose role has only <paramref name="permissions"/>.</summary>
    private sealed class LimitedCall(ICallContext inner, params string[] permissions) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public IDeviceRepository Devices => inner.Devices;

        public IVapixClientFactory Vapix => inner.Vapix;

        public ITaskRunner Tasks => inner.Tasks;

        public IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => permissions.Contains(permission);

        public bool Contains(IDeviceInfo device) => inner.Contains(device);
    }

    /// <summary>Records whose next write runs <see cref="DuringNextWrite"/> and then fails like a locked database.</summary>
    private sealed class FailingRecords : IPluginRecords
    {
        private readonly MemoryPluginRecords _inner = new();

        public Action? DuringNextWrite { get; set; }

        public Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct) => _inner.ListAsync(collection, ct);

        public Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
        {
            if (DuringNextWrite is { } during)
            {
                DuringNextWrite = null;
                during();
                throw new IOException("database is locked");
            }

            return _inner.SetManyAsync(collection, records, ct);
        }

        public Task ClearAsync(string collection, CancellationToken ct) => _inner.ClearAsync(collection, ct);
    }
}
