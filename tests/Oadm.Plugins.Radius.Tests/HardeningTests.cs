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
/// groups"): only certificates the OADM CA issued itself, fail closed without PKI, what a group user sees, requests without EAP, the device list after a miss, maintenance asked for
/// during a round, a stable server certificate, one socket per address, the log's search and Delete all.
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
        Assert.Equal(0, state.PoliciesApply);
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

    [Fact]
    public async Task AnAddressTheHostLeftOutIsListenedAgainByTheNextCheck()
    {
        // The host leaves out an address that is not ready yet (an IPv6 address still checked) while the others serve: the
        // 5 s check sees it missing from the bound addresses and listens again.
        var v6 = IPAddress.Parse("fd00::17");
        var refused = 0;
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.Interfaces.Cards.Add(new ServerNetworkInterface("eth1", "eth1", "eth1", [v6], IsUp: true, IsLoopback: false));
            h.Ctx.FakeNetwork.RefuseAddress = a => a.Equals(v6) && Interlocked.Increment(ref refused) == 1; // once
        });
        var first = harness.Listener;
        Assert.Equal([IPAddress.Parse("10.0.0.17"), v6], harness.Ctx.FakeNetwork.LastUdpAddresses);
        Assert.Equal([IPAddress.Parse("10.0.0.17")], first.BoundAddresses);

        harness.Time.Advance(TimeSpan.FromSeconds(5));

        await Eventually.TrueAsync(() => !ReferenceEquals(first, harness.Service.Listener), "listening again");
        Assert.True(first.IsDisposed);
        Assert.Equal([IPAddress.Parse("10.0.0.17"), v6], harness.Listener.BoundAddresses);
        Assert.Equal(2, harness.Ctx.FakeNetwork.Listeners.Count);
    }

    [Fact]
    public async Task AnAddressThatNeverBindsIsTriedAgainByThe30SecondRetry()
    {
        // Once at the next 5 s check, then with the 30 s retry: a card that never takes the address does not reopen the
        // listener every 5 s.
        var v6 = IPAddress.Parse("fd00::17");
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.Interfaces.Cards.Add(new ServerNetworkInterface("eth1", "eth1", "eth1", [v6], IsUp: true, IsLoopback: false));
            h.Ctx.FakeNetwork.RefuseAddress = a => a.Equals(v6);
        });
        var listeners = harness.Ctx.FakeNetwork.Listeners;

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await Eventually.TrueAsync(() => listeners.Count == 2 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "listening again once");
        for (var i = 0; i < 5; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(5)); // 10 s .. 30 s: only the 30 s retry listens again
        }

        await Eventually.TrueAsync(() => listeners.Count == 3 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "the 30 s retry");
        Assert.Equal([IPAddress.Parse("10.0.0.17")], harness.Listener.BoundAddresses); // the IPv4 address serves meanwhile
    }

    [Fact]
    public async Task AnAddressThatNeverBindsIsNoLongerTriedAfterThreeTriesUntilTheAddressesChange()
    {
        var v6 = IPAddress.Parse("fd00::17");
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.AddDevice(Serial);
            h.Interfaces.Cards.Add(new ServerNetworkInterface("eth1", "eth1", "eth1", [v6], IsUp: true, IsLoopback: false));
            h.Ctx.FakeNetwork.RefuseAddress = a => a.Equals(v6);
        });
        var listeners = harness.Ctx.FakeNetwork.Listeners;

        // The 5 s check, then the 30 s retries at 30 s and 60 s: three tries.
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await Eventually.TrueAsync(() => listeners.Count == 2 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "the 5 s check");
        harness.Time.Advance(TimeSpan.FromSeconds(25));
        await Eventually.TrueAsync(() => listeners.Count == 3 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "the 30 s retry");
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => listeners.Count == 4 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "the 60 s retry");

        // Then no more: the 90 s and 120 s retries keep the listener (the next one would be the fifth).
        var kept = harness.Service.Listener;
        for (var i = 0; i < 12; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(5));
        }

        await Eventually.TrueAsync(() => harness.Service.ListenRetriesSkipped == 2, "the 90 s and 120 s retries skipped");
        Assert.Equal(4, listeners.Count);
        Assert.Same(kept, harness.Service.Listener);
        Assert.Equal([IPAddress.Parse("10.0.0.17")], harness.Listener.BoundAddresses); // the IPv4 address still serves

        // A new address: listened on at once (5 s check), and the missing one is tried again.
        harness.Interfaces.Cards.Add(FixedInterfaces.Card("eth2", "10.0.5.1"));
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await Eventually.TrueAsync(() => listeners.Count == 5 && ReferenceEquals(listeners.Last(), harness.Service.Listener), "the new address");
        Assert.Contains(IPAddress.Parse("10.0.5.1"), harness.Ctx.FakeNetwork.LastUdpAddresses);
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
        var listed = RadiusPolicies.CipherSuites;
        var rsa = listed.Single(s => s.Id == CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256);
        var ecdsa = listed.Single(s => s.Id == CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256);

        Assert.False(rsa.Usable);
        Assert.True(ecdsa.Usable);
        Assert.All(listed.Where(s => s.Tls13), s => Assert.True(s.Usable));
        var server = RadiusPolicies.DefaultServerPolicy();
        Assert.Equal(new Pki.Policies.PolicyFieldError(Pki.Policies.PolicyFields.CipherSuites, Pki.Policies.PolicyRules.CheckCipherSuite),
            Pki.Policies.PolicyRules.Validate(server with { CipherSuites = [rsa.Id] }, listed));
        Assert.Null(Pki.Policies.PolicyRules.Validate(server with { CipherSuites = [rsa.Id, ecdsa.Id] }, listed));
        Assert.NotNull(RadiusPolicies.ValidateTls(server with { MinimumTls = "1.2", CipherSuites = [rsa.Id] })); // the server key's own check
        Assert.Null(RadiusPolicies.ValidateTls(server));
    }

    /// <summary>A user's call whose role has only <paramref name="permissions"/>.</summary>
    internal sealed class LimitedCall(ICallContext inner, params string[] permissions) : ICallContext
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
