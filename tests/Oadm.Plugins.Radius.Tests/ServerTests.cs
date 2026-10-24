using System.Text.Json;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The 802.1X server end to end through the plugin (docs/spec/radius-server.md): a switch sends datagrams to the in-memory
/// UDP listener, a BouncyCastle client plays the camera; who passes, the log, the Sessions tab, the event rules, switches
/// changed while sessions run, VLANs, sites, the license and the server certificate.
/// </summary>
public sealed class ServerTests
{
    private const string Serial = "ACCC8E123456";

    private static TestSupplicant Camera(PluginHarness harness, TestCertificate certificate, ProtocolVersion[]? versions = null) =>
        new(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate, Versions = versions ?? [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12] });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AManagedCameraWithAnOadmCertificateIsAuthenticated(bool tls13)
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var certificate = harness.Ca.Camera(Serial);
        var camera = Camera(harness, certificate, tls13 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12]);

        var conversation = await harness.Switch().RunAsync(camera);

        Assert.Equal(RadiusCode.AccessAccept, conversation.Final);
        Assert.Equal(EapCode.Success, camera.Result);
        Assert.Equal(camera.Msk, conversation.MskFromAccept); // the switch gets the keys the camera derived
        harness.Tick();
        var log = await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null));
        var row = Assert.Single(log.Rows);
        Assert.Equal(LogResults.Authenticated, row.Result);
        Assert.Equal("Core switch", row.SwitchName);
        Assert.Equal("GigabitEthernet1/0/5", row.Port);
        Assert.Equal("AC:CC:8E:12:34:56", row.Mac);
        Assert.Equal(harness.Ctx.DeviceList[0].Id, row.DeviceId);
        Assert.False(row.DeviceGuess);
        Assert.Null(row.Warning);
        Assert.Equal(TestAuthority.SerialOf(certificate), row.CertificateSerial);
        Assert.True(row.HasCertificate);
        var der = await harness.CallAsync<LogCertificate>(RadiusMethods.GetLogCertificate, new LogEntryRef(row.Id));
        Assert.Equal(certificate.Leaf, Convert.FromBase64String(der.Der));
        var fired = Assert.Single(harness.Publisher.Fired);
        Assert.Equal(RadiusService.AuthenticatedEvent, fired.Type);
        Assert.Equal(harness.Ctx.DeviceList[0].Id, fired.Device);
        Assert.Equal("Core switch", fired.Data["switch"]);
        var switches = await harness.CallAsync<SwitchList>(RadiusMethods.ListSwitches);
        Assert.Equal(SwitchStates.Answered, Assert.Single(switches.Switches).State);
    }

    [Fact]
    public async Task ACameraWithItsAxisDeviceIdIsAuthenticated()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var camera = Camera(harness, harness.DeviceIds.Device(Serial));

        var conversation = await harness.Switch().RunAsync(camera);

        Assert.Equal(RadiusCode.AccessAccept, conversation.Final);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "unknown CA", "Client certificate not issued by a trusted CA (TLS alert 48, unknown CA)" },
        { "unmanaged", "Certificate of a device OADM does not manage (ACCC8E999999)" },
        { "revoked", "Certificate revoked on 2026-11-02 (Key compromised)" },
        { "expired", "Client certificate expired on " },
        { "server use", "Client certificate not allowed for network access" },
        { "two devices", "Client certificate names no device" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ACertificateThatDoesNotPassIsRefusedWithItsReason(string kind, string reason)
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        using var foreign = new TestAuthority("Foreign CA");
        var certificate = kind switch
        {
            "unknown CA" => foreign.Camera(Serial),
            "unmanaged" => harness.Ca.Camera("ACCC8E999999"),
            "expired" => harness.Ca.Camera(Serial, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1)),
            "server use" => harness.Ca.Camera(Serial, usage: "1.3.6.1.5.5.7.3.1"),
            "two devices" => harness.Ca.Camera(Serial, moreNames: ["axis-accc8e654321.local"]),
            _ => harness.Ca.Camera(Serial),
        };
        if (kind == "revoked")
        {
            harness.Ca.Revoke(TestAuthority.SerialOf(certificate), harness.Ctx.DeviceList[0].Id);
        }

        var conversation = await harness.Switch().RunAsync(Camera(harness, certificate));

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        harness.Tick();
        var row = Assert.Single((await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows);
        Assert.Equal(LogResults.Failed, row.Result);
        Assert.StartsWith(reason, row.Reason, StringComparison.Ordinal);
        var fired = Assert.Single(harness.Publisher.Fired);
        Assert.Equal(RadiusService.FailedEvent, fired.Type);
        Assert.StartsWith(reason, fired.Data["reason"], StringComparison.Ordinal);
        // The device the certificate named counts only for a revoked certificate of a managed device.
        Assert.Equal(kind == "revoked" ? harness.Ctx.DeviceList[0].Id : null, fired.Device);
    }

    [Fact]
    public async Task ARemovedDeviceIsRefusedAtItsNextAuthentication()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var certificate = harness.Ca.Camera(Serial);
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, certificate))).Final);

        harness.Ctx.DeviceList.Clear();
        await harness.RefreshDevicesAsync();

        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, certificate))).Final);
    }

    [Fact]
    public async Task AMacAddressOfAnotherDevicePassesWithAWarningAndNoFailureEvent()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var radiusSwitch = new PluginSwitch(harness, harness.Listener, new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.2"), 51812), System.Text.Encoding.ASCII.GetBytes(PluginHarness.Secret))
        {
            CallingStationId = "ac-cc-8e-65-43-21",
        };

        Assert.Equal(RadiusCode.AccessAccept, (await radiusSwitch.RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);

        harness.Tick();
        var row = Assert.Single((await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows);
        Assert.Equal("MAC address AC:CC:8E:65:43:21 does not match the certificate of ACCC8E123456", row.Warning);
        Assert.Equal(RadiusService.AuthenticatedEvent, Assert.Single(harness.Publisher.Fired).Type);
    }

    [Fact]
    public async Task ADeviceOfAnotherSiteIsRefusedAndOneOfTheSwitchsSiteAuthenticatesThroughTheRelay()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var site = harness.Ctx.FakeSites.Add("Building B");
        site.AllowedPorts.Add((SiteProtocol.Udp, 1812));
        site.Network.LocalAddress = System.Net.IPAddress.Parse("10.1.0.5");
        var atSite = harness.AddDevice(Serial, site.Id);
        var local = harness.AddDevice("ACCC8E654321");
        await harness.StartAsync();

        var row = await harness.AddSwitchAsync("Site switch", "192.168.1.2", site.Id);

        Assert.Equal("10.1.0.5", row.RadiusServerAddress);
        Assert.Equal("Building B", row.SiteName);
        var relay = (InMemoryUdpRelay)harness.Service.SiteRelay(site.Id)!;
        Assert.Equal(1812, relay.Port);
        var siteSwitch = harness.Switch("192.168.1.2", relay: relay);
        Assert.Equal(RadiusCode.AccessAccept, (await siteSwitch.RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
        Assert.Equal(RadiusCode.AccessReject, (await siteSwitch.RunAsync(Camera(harness, harness.Ca.Camera(local.Serial)))).Final);
        harness.Tick();
        var log = await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null));
        Assert.Equal("The device belongs to another site", log.Rows[0].Reason);
        Assert.Equal(local.Id, log.Rows[0].DeviceId);

        // The same address on this server's network is another switch: unknown here.
        var stranger = harness.Switch("192.168.1.2");
        Assert.Null((await stranger.SendAsync([], null, TimeSpan.FromMilliseconds(1))).Answer);
        await Eventually.TrueAsync(() => harness.Service.GetState(true).UnknownRequests.Count == 1, "the request of the unknown sender counted");
        Assert.Equal("192.168.1.2", harness.Service.GetState(true).UnknownRequests.LastAddress);

        // The site's last switch goes: its relay is released.
        await harness.CallAsync(RadiusMethods.DeleteSwitch, new SwitchRef(row.Id));
        Assert.True(relay.IsReleased);
        Assert.Null(harness.Service.SiteRelay(site.Id));
    }

    [Fact]
    public async Task ASiteWhoseNodeHasNotAllowedThePortIsListed()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var site = harness.Ctx.FakeSites.Add("Building B");

        await harness.AddSwitchAsync("Site switch", "192.168.1.2", site.Id);

        var state = await harness.CallAsync<RadiusState>(RadiusMethods.GetState);
        Assert.Equal(new SiteRef(site.Id, "Building B"), Assert.Single(state.SitesNotAllowed));
    }

    [Fact]
    public async Task ASwitchRemovedWhileItsSessionRunsEndsItAndTheOtherSwitchKeepsItsSession()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        var removed = await harness.AddSwitchAsync("Old switch", "10.0.0.2");
        await harness.AddSwitchAsync("Kept switch", "10.0.0.3");
        var first = harness.Switch("10.0.0.2");
        var second = harness.Switch("10.0.0.3");
        var identity = EapPacket.Response(1, EapType.Identity, "axis-accc8e123456"u8.ToArray()).ToBytes();
        var (startA, _) = await first.SendAsync(identity, null);
        var (startB, _) = await second.SendAsync(identity, null);
        Assert.Equal(RadiusCode.AccessChallenge, startA!.Code);
        Assert.Equal(RadiusCode.AccessChallenge, startB!.Code);
        harness.Tick();
        Assert.Equal(2, (await harness.CallAsync<SessionList>(RadiusMethods.ListSessions)).Sessions.Count);

        await harness.CallAsync(RadiusMethods.DeleteSwitch, new SwitchRef(removed.Id));
        harness.Tick();

        var sessions = (await harness.CallAsync<SessionList>(RadiusMethods.ListSessions)).Sessions;
        Assert.Equal("Abandoned", sessions.Single(s => s.SwitchName == "Old switch").StateText);
        Assert.Equal("Starting TLS", sessions.Single(s => s.SwitchName == "Kept switch").StateText);
        // The removed switch's next request is dropped as from an unknown sender; the kept one goes on.
        Assert.Null((await first.SendAsync(identity, startA.Find(RadiusAttributeType.State)?.Value.ToArray(), TimeSpan.FromMilliseconds(1))).Answer);
        await Eventually.TrueAsync(() => harness.Service.GetState(true).UnknownRequests.Count == 1, "the removed switch counted as unknown");
        var camera = Camera(harness, harness.Ca.Camera(Serial));
        var eap = camera.Respond(EapPacket.Parse(TestSwitch.Join(startB)).Packet!)!.ToBytes();
        var (next, _) = await second.SendAsync(eap, startB.Find(RadiusAttributeType.State)?.Value.ToArray());
        Assert.Equal(RadiusCode.AccessChallenge, next!.Code);

        // The ended session leaves the tab after 10 s and is in the log.
        harness.Tick(21);
        sessions = (await harness.CallAsync<SessionList>(RadiusMethods.ListSessions)).Sessions;
        Assert.DoesNotContain(sessions, s => s.SwitchName == "Old switch");
        var log = await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("Old switch"));
        Assert.Equal("The switch was removed", Assert.Single(log.Rows).Reason);
    }

    [Fact]
    public async Task ASwitchAddedWhileTheServerRunsIsAnsweredAtOnce()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        var radiusSwitch = harness.Switch("10.0.0.9");
        var identity = EapPacket.Response(1, EapType.Identity, "axis-accc8e123456"u8.ToArray()).ToBytes();
        Assert.Null((await radiusSwitch.SendAsync(identity, null, TimeSpan.FromMilliseconds(1))).Answer);
        await Eventually.TrueAsync(() => harness.Service.GetState(true).UnknownRequests.Count == 1, "unknown before it was added");

        await harness.AddSwitchAsync("New switch", "10.0.0.9");

        Assert.Equal(RadiusCode.AccessAccept, (await radiusSwitch.RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
    }

    [Fact]
    public async Task AWrongSecretIsDroppedAndShownOnTheSwitch()
    {
        await using var harness = await PluginHarness.CreateAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var wrong = harness.Switch(secret: "another-secret-of-24-chars");

        Assert.Null((await wrong.SendAsync([], null, TimeSpan.FromMilliseconds(1))).Answer);

        await Eventually.TrueAsync(() => harness.Service.ListSwitches().Switches[0].DroppedCount == 1, "the dropped request counted");
        Assert.Equal(SwitchStates.WrongSecret, harness.Service.ListSwitches().Switches[0].State);
    }

    [Fact]
    public async Task WithoutOadmProNothingIsAnswered()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        harness.Pro.Current = ProLicenseInfo.None;

        Assert.Null((await harness.Switch().SendAsync([], null, TimeSpan.FromMilliseconds(200))).Answer);
        Assert.Equal(RadiusTexts.NotPro, (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).StatusText);
    }

    [Fact]
    public async Task TheStateSaysWhatIsMissingAndRunningWhenAllIsThere()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var state = await harness.CallAsync<RadiusState>(RadiusMethods.GetState);
        Assert.True(state.Running);
        Assert.Equal("Running on port 1812", state.StatusText);
        Assert.Contains("10.0.0.17", state.ServerCertificate!.Names);
        Assert.Contains("oadm-test", state.ServerCertificate.Names);
        Assert.Equal(["all", "eth0"], state.Interfaces.Select(i => i.Id));
        Assert.Equal(RadiusLimits.DefaultLogKept, state.Settings.LogKept);
        Assert.Equal("1.2", state.Settings.MinimumTls);
        Assert.NotEmpty(state.Settings.CipherSuites);
        Assert.DoesNotContain(state.CipherSuitesListed.Where(s => s.Legacy), s => state.Settings.CipherSuites.Contains(s.Id));

        harness.Listener.SetState(RelayState.PortInUse);
        Assert.Equal("Port 1812 is used by another program", (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).StatusText);
    }

    [Fact]
    public async Task WithoutACaTheStateSaysSoAndHandshakesAreRefused()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.Ca.IsReady = false;
            h.AddDevice(Serial);
        });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        Assert.Equal(RadiusTexts.NoCa, (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).StatusText);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
    }

    [Fact]
    public async Task TheServerCertificateIsKeptAcrossRestartsAndRenewed30DaysBeforeItEnds()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var first = (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).ServerCertificate!;
        Assert.Equal(1, harness.Ca.SignCount);
        var stored = harness.Ctx.PluginSettings.Values[RadiusStore.ServerCertificateKey];
        Assert.Contains("enc:radius:server-key:", stored, StringComparison.Ordinal);

        await harness.Plugin.StopAsync(CancellationToken.None);
        await harness.StartAsync();
        var again = (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).ServerCertificate!;
        Assert.Equal(first.NotAfterUtc, again.NotAfterUtc);
        Assert.Equal(first.Names, again.Names);
        Assert.Equal(1, harness.Ca.SignCount);

        // The stored certificate also reaches the handshakes after the restart (it once showed on the page while every
        // camera got "The 802.1X server has no certificate").
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);

        // 29 days before its end (stopped meanwhile: the clock jumps without running a year of ticks).
        await harness.Plugin.StopAsync(CancellationToken.None);
        harness.Time.Advance(ServerIdentityKeeper.Validity - TimeSpan.FromDays(29));
        await harness.StartAsync();
        Assert.Equal(2, harness.Ca.SignCount);
    }

    [Fact]
    public async Task ANewCaGivesANewServerCertificateAndCertificatesOfThePreviousCaStillPass()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var oldCertificate = harness.Ca.Camera(Serial);
        var newCa = new TestAuthority("OADM new CA");
        newCa.Previous.Add([harness.Ca.CaCertificate]);
        harness.Ctx.FakeServices.Withdraw<Oadm.Sdk.Certificates.IDeviceCertificateAuthority>();
        harness.Ctx.FakeServices.Offer<Oadm.Sdk.Certificates.IDeviceCertificateAuthority>(newCa);

        await harness.Service.MaintainAsync(CancellationToken.None);

        Assert.Equal(1, newCa.SignCount);
        var camera = new TestSupplicant(new SupplicantOptions { Certificate = oldCertificate, TrustedCa = newCa.CaCertificate });
        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(camera)).Final);
        newCa.Dispose();
    }

    [Fact]
    public async Task SecretsAreStoredEncryptedAndNeverReturned()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var row = await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        var stored = harness.Ctx.PluginSettings.Values[RadiusStore.SwitchesKey];
        Assert.DoesNotContain(PluginHarness.Secret, stored, StringComparison.Ordinal);
        Assert.Contains("enc:radius:switch:", stored, StringComparison.Ordinal);
        var listed = await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, RadiusMethods.ListSwitches, null);
        Assert.DoesNotContain(PluginHarness.Secret, listed!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", JsonDocument.Parse(listed!).RootElement.GetProperty("switches")[0].EnumerateObject().Select(p => p.Name));

        // An edit with an empty secret keeps it: the switch is still answered.
        await harness.CallAsync<SwitchRow>(RadiusMethods.SaveSwitch, new SaveSwitchRequest { Id = row.Id, Name = "Renamed", Address = "10.0.0.2" });
        var (answer, _) = await harness.Switch().SendAsync([], null);
        Assert.Equal(RadiusCode.AccessChallenge, answer!.Code);
        Assert.Equal("Renamed", Assert.Single(harness.Service.ListSwitches().Switches).Name);

        // After a restart the secret is read back from the store.
        await harness.Plugin.StopAsync(CancellationToken.None);
        await harness.StartAsync();
        Assert.Equal(RadiusCode.AccessChallenge, (await harness.Switch().SendAsync([], null)).Answer!.Code);
    }

    [Fact]
    public async Task SwitchInputIsCheckedAsOnThePage()
    {
        await using var harness = await PluginHarness.CreateAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        async Task<string> Refused(SaveSwitchRequest request) =>
            (await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<SwitchRow>(RadiusMethods.SaveSwitch, request))).Message.Split(" (Parameter")[0];

        Assert.Equal(RadiusRules.NameTaken, await Refused(new SaveSwitchRequest { Name = "core SWITCH", Address = "10.0.0.3", Secret = PluginHarness.Secret }));
        Assert.Equal(RadiusRules.AddressTaken, await Refused(new SaveSwitchRequest { Name = "Other", Address = "10.0.0.2", Secret = PluginHarness.Secret }));
        Assert.Equal(RadiusRules.SecretTooShort, await Refused(new SaveSwitchRequest { Name = "Other", Address = "10.0.0.3", Secret = "short" }));
        Assert.Equal(RadiusRules.EnterSecret, await Refused(new SaveSwitchRequest { Name = "Other", Address = "10.0.0.3" }));
        Assert.Equal(RadiusRules.EnterAddress, await Refused(new SaveSwitchRequest { Name = "Other", Address = "not a name!", Secret = PluginHarness.Secret }));
        Assert.Equal(RadiusTexts.HostNameUnknown, await Refused(new SaveSwitchRequest { Name = "Other", Address = "switch.example", Secret = PluginHarness.Secret }));
        Assert.Equal(RadiusTexts.SiteGone, await Refused(new SaveSwitchRequest { Name = "Other", Address = "10.0.0.3", SiteId = Guid.NewGuid(), Secret = PluginHarness.Secret }));

        var generated = await harness.CallAsync<GeneratedSecret>(RadiusMethods.GenerateSecret);
        Assert.Equal(24, generated.Secret.Length);
        Assert.Null(RadiusRules.Validate(new SaveSwitchRequest { Name = "x", Address = "10.0.0.9", Secret = generated.Secret }, [], isNew: true));
    }

    [Fact]
    public async Task ASwitchByHostNameIsFoundByTheAddressItResolvesTo()
    {
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.Ctx.FakeNetwork.Local.Names["switch.example"] = [System.Net.IPAddress.Parse("10.0.0.44")];
            h.AddDevice(Serial);
        });
        await harness.AddSwitchAsync("Named switch", "switch.example");

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch("10.0.0.44").RunAsync(Camera(harness, harness.Ca.Camera(Serial)))).Final);
    }

    [Fact]
    public async Task SettingsAreCheckedAppliedAndTheMinimumVersionRefusesOlderCameras()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var state = await harness.CallAsync<RadiusState>(RadiusMethods.GetState);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with { LogKept = 10 }));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with { CipherSuites = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with { ListenOn = "gone" }));
        // TLS 1.3 only, but just TLS 1.2 suites checked: nothing usable.
        await Assert.ThrowsAsync<ArgumentException>(() => harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with
        {
            MinimumTls = "1.3",
            CipherSuites = [.. state.CipherSuitesListed.Where(s => !s.Tls13 && !s.Legacy).Select(s => s.Id)],
        }));

        var saved = await harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with { MinimumTls = "1.3", LogKept = 2000 });

        Assert.Equal("1.3", saved.Settings.MinimumTls);
        Assert.Equal(2000, saved.Settings.LogKept);
        var oldCamera = Camera(harness, harness.Ca.Camera(Serial), [ProtocolVersion.TLSv12]);
        Assert.Equal(RadiusCode.AccessReject, (await harness.Switch().RunAsync(oldCamera)).Final);
        harness.Tick();
        Assert.Contains("TLS version not supported (TLS alert 70, protocol version)", (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListenOnOneInterfaceBindsItsAddressesAndANewPortMovesTheListener()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var state = await harness.CallAsync<RadiusState>(RadiusMethods.GetState);
        var first = harness.Listener;

        await harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, state.Settings with { ListenOn = "eth0", Port = 11812 });

        Assert.True(first.IsReleased);
        Assert.Equal(11812, harness.Listener.Port);
        Assert.Equal([System.Net.IPAddress.Parse("10.0.0.17")], harness.Ctx.FakeNetwork.LastUdpAddresses);
        var row = await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        Assert.Equal("10.0.0.17", row.RadiusServerAddress);

        // The card goes away: the retry round closes the listener and the status says so.
        harness.Interfaces.Cards.Clear();
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => harness.Service.GetState(true).StatusText == "Interface eth0 is not available", "the status names the missing interface");
    }

    [Fact]
    public async Task TheLogKeepsTheNewestEntriesSearchesEveryColumnAndDeleteAllEmptiesIt()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)));
        using var foreign = new TestAuthority("Foreign CA");
        await harness.Switch().RunAsync(Camera(harness, foreign.Camera(Serial)));
        harness.Tick();

        Assert.Equal(2, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Total);
        Assert.Equal(LogResults.Failed, Assert.Single((await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("TRUSTED CA"))).Rows).Result);
        Assert.Equal(LogResults.Authenticated, Assert.Single((await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("authenticated"))).Rows).Result);
        Assert.Equal(2, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("gigabitethernet1/0/5"))).Total);

        await harness.Service.Log.FlushAsync(CancellationToken.None);
        Assert.Equal(2, (await harness.Ctx.PluginRecords.ListAsync(RadiusLog.LogCollection, CancellationToken.None)).Count);
        Assert.Equal(2, (await harness.Ctx.PluginRecords.ListAsync(RadiusLog.CertificatesCollection, CancellationToken.None)).Count); // one per certificate

        await harness.CallAsync(RadiusMethods.DeleteLog);
        await harness.Service.Log.FlushAsync(CancellationToken.None);

        Assert.Equal(0, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Total);
        Assert.Empty(await harness.Ctx.PluginRecords.ListAsync(RadiusLog.LogCollection, CancellationToken.None));
        Assert.Contains(harness.Ctx.PublishedEvents, e => e.Topic == RadiusTopics.LogCleared);
    }

    [Fact]
    public async Task TheLogComesBackAfterARestart()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)));
        harness.Tick();
        var before = (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows[0];

        await harness.Plugin.StopAsync(CancellationToken.None);
        await harness.StartAsync();

        var after = Assert.Single((await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows);
        Assert.Equal(before, after);
        Assert.NotEmpty((await harness.CallAsync<LogCertificate>(RadiusMethods.GetLogCertificate, new LogEntryRef(after.Id))).Der);
    }

    [Fact]
    public async Task SessionsArePublishedInBatchesAndAnEndedOneLeavesWithRemoved()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice(Serial));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await harness.Switch().RunAsync(Camera(harness, harness.Ca.Camera(Serial)));

        harness.Tick();
        var rows = harness.Ctx.PublishedEvents.Where(e => e.Topic == RadiusTopics.Sessions)
            .SelectMany(e => RadiusJson.Deserialize<List<SessionRow>>(e.PayloadJson)).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("authenticated", row.State);
        Assert.Equal("Authenticated", row.StateText);
        Assert.Equal("TLS 1.3", row.TlsVersion);
        Assert.Single(harness.Ctx.PublishedEvents, e => e.Topic == RadiusTopics.Log);

        harness.Tick(20);
        var removed = harness.Ctx.PublishedEvents.Where(e => e.Topic == RadiusTopics.Sessions)
            .SelectMany(e => RadiusJson.Deserialize<List<SessionRow>>(e.PayloadJson)).Last();
        Assert.True(removed.Removed);
        Assert.Empty((await harness.CallAsync<SessionList>(RadiusMethods.ListSessions)).Sessions);
    }
}
