using System.Text.Json;
using Oadm.Plugins.Pki.Servers;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;

using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The plugin entry point: its manifest, the device-group canary and the rows of a user limited to a group (docs/spec/device-scope.md).</summary>
public sealed class PluginTests
{
    [Fact]
    public void ThePkiPackageCarriesTheServerWithItsPortAndIds()
    {
        // The 802.1X server is part of the PKI package (docs/spec/pki.md "One plugin"): PKI's manifest lists its site port;
        // the core plugin keeps its id, so its permissions, events and stored data keep their keys.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Oadm.Tests.Shared.TestRepository.Path("plugins", "Oadm.Plugins.Pki", "plugin.json")));
        var root = manifest.RootElement;

        Assert.Equal("oadm.pki", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("alwaysOn").GetBoolean());
        Assert.False(root.TryGetProperty("pro", out _));
        Assert.Equal("full", root.GetProperty("siteSupport").GetString());
        var port = Assert.Single(root.GetProperty("sitePorts").EnumerateArray(), p => p.GetProperty("reason").GetString() == "802.1X server");
        Assert.Equal("udp", port.GetProperty("protocol").GetString());
        Assert.Equal(1812, port.GetProperty("port").GetInt32());

        var plugin = new RadiusPlugin();
        Assert.Equal("oadm.radius", plugin.Id);
        Assert.Equal("802.1X server", plugin.DisplayName);
        Assert.Equal("lock", plugin.IconKey);
        Assert.Equal(CorePluginGroup.Security, plugin.Group);
        Assert.True(plugin.HasPage);
        Assert.Equal(["oadm.radius.authenticated", "oadm.radius.failed"], plugin.EventTypes.Select(t => t.Id));
        Assert.Equal([PermissionKeys.Manage], plugin.Permissions.Select(p => p.Key));
        Assert.Equal(typeof(Oadm.Plugins.Pki.PkiPlugin).Assembly, typeof(RadiusPlugin).Assembly);
    }

    [Fact]
    public async Task OffByDefaultThePortStaysClosedUntilTurnOnAndTurnOffClosesIt()
    {
        // docs/spec/pki.md "One plugin": the server is off by default and runs (UDP 1812 open) only while turned on.
        await using var harness = await PluginHarness.CreateAsync(h => h.TurnedOn = false);
        var listeners = harness.Ctx.FakeNetwork.Listeners;

        Assert.Null(harness.Plugin.Service);
        Assert.Empty(listeners);
        var off = ServerSwitchMethods.Deserialize(await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, ServerSwitchMethods.Get, null));
        Assert.Equal(new ServerSwitchState(false, false, true, ServerSwitchMethods.OffText), off);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.CallAsync<RadiusState>(RadiusMethods.GetState));

        var on = ServerSwitchMethods.Deserialize(await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, ServerSwitchMethods.TurnOn, null));

        Assert.True(on.Running);
        Assert.Equal(1812, Assert.Single(listeners, l => !l.IsDisposed).Port);
        Assert.Equal("Running on port 1812", (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).StatusText);

        await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, ServerSwitchMethods.TurnOff, null);

        Assert.Null(harness.Plugin.Service);
        Assert.All(listeners, l => Assert.True(l.IsDisposed));
        Assert.Equal("false", await harness.Ctx.Settings.GetAsync(ServerSwitch<RadiusService>.SettingKey, CancellationToken.None));
    }

    [Fact]
    public async Task TheSettingsOf41StayTheServersAndTheTakenOverChoiceStartsIt()
    {
        // Stored by 4.1 under the core plugin id oadm.radius; the host took the plugin's choice over as "serverOn".
        await using var harness = await PluginHarness.CreateAsync(h =>
        {
            h.TurnedOn = false;
            h.Ctx.Settings.SetAsync(RadiusStore.ConfigKey, RadiusJson.Serialize(new RadiusSettings { Port = 1813, LogKept = 2000 }), CancellationToken.None).GetAwaiter().GetResult();
            h.Ctx.Settings.SetAsync("serverOn", "true", CancellationToken.None).GetAwaiter().GetResult();
        });

        Assert.Equal(1813, Assert.Single(harness.Ctx.FakeNetwork.Listeners).Port);
        Assert.Equal(2000, (await harness.CallAsync<RadiusState>(RadiusMethods.GetState)).Settings.LogKept);
    }

    [Fact]
    public async Task TheCanaryPasses()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var inside = harness.AddDevice("ACCC8E000001");
        var outside = harness.AddDevice("ACCC8E000002");
        await harness.StartAsync();
        var row = await harness.AddSwitchAsync("Core switch", "10.0.0.2");

        // No method takes a device id: rows of devices are filtered to the caller (own tests below), changes are for users
        // with all devices. Every id field: the switch id (deleteSwitch, saveSwitch), the log row id (getLogCertificate), the
        // sites, tags and serials of the policies (savePolicies, countRevocations), the CA ids (saveTrustedCas).
        await new PluginCanary(harness.Ctx, harness.Plugin, inside, outside)
            .WithoutDevices(RadiusMethods.GetState, "server-wide state, no device data")
            .WithoutDevices(RadiusMethods.ListSwitches, "server-wide switches, no device data")
            .WithoutDevices(RadiusMethods.GetPolicies, "the policies; tag choices of the devices only for all devices")
            .WithoutDevices(RadiusMethods.GetTrustedCas, "PKI's CAs, no device data")
            .WithoutDevices(RadiusMethods.ListSessions, "rows filtered to the caller's devices (GroupUsersSeeOnlyTheRowsOfTheirDevices)")
            .WithoutDevices(RadiusMethods.ListLog, "rows filtered to the caller's devices", """{"search":null}""")
            .WithoutDevices(RadiusMethods.GetLogCertificate, "a row id; rows of other devices answer like unknown ones", """{"id":1}""")
            .WithoutDevices(RadiusMethods.ListSites, "the sites of the caller's devices")
            .AllDevicesOnly(RadiusMethods.SaveSettings, RadiusJson.Serialize(harness.Service.GetState(true).Settings))
            .AllDevicesOnly(RadiusMethods.SaveSwitch, $$"""{"id":"{{row.Id}}","name":"x","address":"10.0.0.9"}""")
            .AllDevicesOnly(RadiusMethods.GenerateSecret)
            .AllDevicesOnly(RadiusMethods.DeleteSwitch, $$"""{"id":"{{row.Id}}"}""")
            .AllDevicesOnly(RadiusMethods.SavePolicies, """{"policies":[]}""")
            .AllDevicesOnly(RadiusMethods.SaveTrustedCas, """{"cas":[]}""")
            .AllDevicesOnly(RadiusMethods.CountRevocations, """{"thisServer":true}""")
            .AllDevicesOnly(RadiusMethods.DeleteLog)
            .RunAsync();
        Assert.Single(harness.Service.ListSwitches().Switches); // the refused delete changed nothing
    }

    [Fact]
    public async Task GroupUsersSeeOnlyTheRowsOfTheirDevices()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var inside = harness.AddDevice("ACCC8E000001", tags: ["Lobby"]);
        var outside = harness.AddDevice("ACCC8E000002", tags: ["Secret tag"]);
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var insideCertificate = harness.Ca.Camera(inside.Serial);
        var outsideCertificate = harness.Ca.Camera(outside.Serial);
        using var foreign = new TestAuthority("Foreign CA");
        foreach (var certificate in new[] { insideCertificate, outsideCertificate, foreign.Camera("ACCC8E999999") })
        {
            var camera = new TestSupplicant(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate });
            await harness.Switch().RunAsync(camera);
        }

        harness.Tick();
        var group = harness.Ctx.CallAsGroup([inside.Id]);

        var log = await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null), group);
        Assert.Equal(inside.Id, Assert.Single(log.Rows).DeviceId);
        Assert.Equal(3, (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Total);
        Assert.Equal(inside.Id, Assert.Single((await harness.CallAsync<SessionList>(RadiusMethods.ListSessions, null, group)).Sessions).DeviceId);
        Assert.Equal(3, (await harness.CallAsync<SessionList>(RadiusMethods.ListSessions)).Sessions.Count);

        // A row of another device and one of an unknown device answer like a row no longer kept.
        var all = (await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(null))).Rows;
        foreach (var other in all.Where(r => r.DeviceId != inside.Id))
        {
            var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.CallAsync<LogCertificate>(RadiusMethods.GetLogCertificate, new LogEntryRef(other.Id), group));
            Assert.Equal(RadiusTexts.LogEntryGone, refused.Message);
        }

        Assert.NotEmpty((await harness.CallAsync<LogCertificate>(RadiusMethods.GetLogCertificate, new LogEntryRef(log.Rows[0].Id), group)).Der);

        // The events the group's page gets: only rows of its devices.
        var seen = harness.Ctx.EventsSeenBy(group).Where(e => e.Topic is RadiusTopics.Log or RadiusTopics.Sessions)
            .SelectMany(e => JsonDocument.Parse(e.PayloadJson!).RootElement.EnumerateArray().Select(r => r.GetProperty("deviceId").GetString())).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, id => Assert.Equal(inside.Id.ToString(), id));

        // Tag choices of the devices only for users with all devices.
        Assert.Empty((await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies, null, group)).TagChoices);
        Assert.Equal(["Lobby", "Secret tag"], (await harness.CallAsync<PolicySettings>(RadiusMethods.GetPolicies)).TagChoices.Select(t => t.Name));
        Assert.False((await harness.CallAsync<RadiusState>(RadiusMethods.GetState, null, group)).HasAllDevices);
        Assert.Empty(harness.Ctx.RefusedAccesses);
    }

    [Fact]
    public async Task ListSitesNamesTheBoundSitesAndAGroupUserGetsTheSitesOfTheGroup()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var siteA = harness.Ctx.FakeSites.Add("Building A");
        var siteB = harness.Ctx.FakeSites.Add("Building B");
        var device = harness.AddDevice("ACCC8E000001", siteB.Id);
        await harness.StartAsync();

        Assert.Equal(["Building A", "Building B"], (await harness.CallAsync<SiteList>(RadiusMethods.ListSites)).Sites.Select(s => s.SiteName));
        var group = harness.Ctx.CallAsGroup([device.Id]);
        Assert.Equal(siteB.Id, Assert.Single((await harness.CallAsync<SiteList>(RadiusMethods.ListSites, null, group)).Sites).SiteId);
        Assert.NotEqual(siteA.Id, siteB.Id);
    }

    [Theory]
    [InlineData("10.0.0.2", true)]
    [InlineData("fe80::1", true)]
    [InlineData("switch-1.example", true)]
    [InlineData("10", false)]
    [InlineData("10.0.0", false)]
    [InlineData("10.0.0.256", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("::zz", false)]
    [InlineData("bad name", false)]
    public void AnAddressIsAFullIpAddressOrAHostName(string address, bool valid) =>
        Assert.Equal(valid, RadiusRules.IsAddress(address));

    [Fact]
    public async Task TheEventTypesFireForTheRightDevice()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.AddDevice("ACCC8E000001"));
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var camera = new TestSupplicant(new SupplicantOptions { Certificate = harness.Ca.Camera("ACCC8E000001"), TrustedCa = harness.Ca.CaCertificate });

        Assert.Equal(RadiusCode.AccessAccept, (await harness.Switch().RunAsync(camera)).Final);
        harness.Tick();

        var fired = Assert.Single(harness.Publisher.Fired);
        Assert.Equal("GigabitEthernet1/0/5", fired.Data["port"]);
        Assert.Equal(string.Empty, fired.Data["vlan"]);
    }
}
