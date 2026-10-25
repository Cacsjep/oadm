using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Users limited to a device group (docs/spec/device-scope.md): the CA is server-wide, so every method except reading the
/// state is for users with all devices; the state counts only the group's devices; a device a group user added gets no
/// host name in its certificates.
/// </summary>
public sealed class DeviceGroupTests
{
    private static readonly string[] ManageMethods =
    [
        PkiMethods.Generate, PkiMethods.Import, PkiMethods.PreviewReplace, PkiMethods.ExportPublic, PkiMethods.ExportPrevious,
        PkiMethods.RemovePrevious, PkiMethods.Backup, PkiMethods.InstallServerTrust,
        PkiMethods.Revoke, PkiMethods.RevokeDevice,
    ];

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(ctx, CancellationToken.None);

        var canary = new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(PkiMethods.GetState, "the CA and the settings; the counts of issued certificates only of the user's devices")
            .WithoutDevices(PkiMethods.GetCertificates, "the issued certificates only of the user's devices (no device ids in the payload)", "{}")
            .WithoutDevices(PkiMethods.GetCertificate, "a certificate of another device answers like one that is not listed", """{"serialNumber":"01"}""");
        foreach (var method in ManageMethods)
        {
            canary.AllDevicesOnly(method, "{}");
        }

        await canary.RunAsync();
    }

    [Fact]
    public async Task The_state_of_a_group_user_counts_only_the_groups_devices_and_follows_changes()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice());
        var outside = ctx.Add(new FakeDevice());
        var now = DateTime.UtcNow;
        await ctx.PluginSettings.SetAsync(PkiStore.IssuedKey, PkiJson.Serialize(new[]
        {
            new IssuedCertificate { SerialNumber = "01", DeviceId = inside.Id, CaId = "old", IssuedUtc = now, NotAfterUtc = now.AddDays(300) },
            new IssuedCertificate { SerialNumber = "02", DeviceId = outside.Id, CaId = "old", IssuedUtc = now, NotAfterUtc = now.AddDays(300) },
        }), CancellationToken.None);
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", key);
        var admin = ctx.CallAsAllDevices();

        var seen = PkiJson.Deserialize<PkiState>(await ctx.InvokeAsync(plugin, group, PkiMethods.GetState, null));
        Assert.Equal((1, 1), (seen.IssuedDevices, seen.DevicesWithPreviousCa));
        Assert.Equal(2, PkiJson.Deserialize<PkiState>(await ctx.InvokeAsync(plugin, admin, PkiMethods.GetState, null)).IssuedDevices);
        Assert.Empty(ctx.RefusedAccesses);

        // A change of the policies: the group's page gets the state with its own counts, users with all devices the full one.
        var https = plugin.Service!.Policies.FirstOf(Policies.PolicyKinds.HttpsCertificate)!;
        await ctx.InvokeAsync(plugin, admin, Policies.PolicyMethods.SavePolicy, PkiJson.Serialize(https with { ValidDays = 45 }));
        await Eventually.TrueAsync(() => ctx.EventsSeenBy(group).Any(e => e.Topic == PkiMethods.StateTopic), "the state after the change");
        PkiState Last(ICallContext watcher) => PkiJson.Deserialize<PkiState>(ctx.EventsSeenBy(watcher).Last(e => e.Topic == PkiMethods.StateTopic).PayloadJson);
        Assert.Equal(1, Last(group).IssuedDevices);
        Assert.Equal(2, Last(admin).IssuedDevices);
        Assert.DoesNotContain(ctx.EventsSeenBy(ctx.CallAsGroup([outside.Id])), e => e.Topic == PkiMethods.StateTopic); // never read the page
    }

    [Fact]
    public async Task A_group_user_sees_only_the_certificates_of_the_groups_devices_and_cannot_revoke()
    {
        var ctx = new FakeCoreContext() { Secrets = TestSecrets.Create() };
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" });
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000001" });
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        await plugin.Service!.Background;
        var authority = ctx.FakeServices.Find<Oadm.Sdk.Certificates.IDeviceCertificateAuthority>()!;
        var mine = await RevocationTests.SignAsync(authority, inside.Id);
        var theirs = await RevocationTests.SignAsync(authority, outside.Id, "ACCC8E000001");
        var gone = await RevocationTests.SignAsync(authority, Guid.NewGuid(), "ACCC8E000002"); // a device OADM no longer manages
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", key);
        var admin = ctx.CallAsAllDevices();

        var seen = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(plugin, group, PkiMethods.GetCertificates, "{}"));
        var row = Assert.Single(seen.Rows);
        Assert.Equal((inside.Id, "10.0.0.48", true), (row.DeviceId, row.Device, row.DeviceManaged));
        async Task<int> CountAsync(ICallContext call, string search) =>
            PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(plugin, call, PkiMethods.GetCertificates, PkiJson.Serialize(new CertificatesRequest { Search = search }))).Total;
        Assert.Equal(1, await CountAsync(group, "10.0.0.48"));
        Assert.Equal(0, await CountAsync(group, "10.0.0.49"));
        Assert.Equal(1, await CountAsync(admin, "10.0.0.49"));
        Assert.Equal(0, await CountAsync(group, "ACCC8E"));
        var all = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(plugin, admin, PkiMethods.GetCertificates, "{}"));
        Assert.Equal(2, all.Rows.Count(r => r.Kind == Oadm.Sdk.Certificates.ListedCertificateKind.Device)); // and the CAs, which a group user never sees
        Assert.All(seen.Rows, r => Assert.Equal(Oadm.Sdk.Certificates.ListedCertificateKind.Device, r.Kind));
        Assert.DoesNotContain(all.Rows, r => r.SerialNumber == Oadm.Sdk.Certificates.CertificateSerials.Normalize(gone.SerialNumber)); // its camera is not managed: hidden

        // A certificate of another device answers like an unknown one.
        async Task<string> DetailsAsync(ICallContext call, string serial) =>
            PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(plugin, call, PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(serial)))).Error ?? "shown";
        Assert.Equal("shown", await DetailsAsync(group, mine.SerialNumber));
        Assert.Equal(PkiCertificateTexts.NotListed, await DetailsAsync(group, theirs.SerialNumber));
        Assert.Equal(PkiCertificateTexts.NotListed, await DetailsAsync(group, gone.SerialNumber));
        Assert.Equal(PkiCertificateTexts.NotListed, await DetailsAsync(group, "0BAD"));
        Assert.Equal("shown", await DetailsAsync(admin, theirs.SerialNumber));
        Assert.Empty(ctx.RefusedAccesses);

        // Revoking is for users with all devices; the group's page hears of it with its own audience.
        Assert.True(plugin.RequiresAllDevices(PkiMethods.Revoke));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, group, PkiMethods.Revoke, PkiJson.Serialize(new RevokeRequest([mine.SerialNumber], Oadm.Sdk.Certificates.RevocationReason.Other))));
        Assert.False(authority.IsRevoked(mine.SerialNumber));
        await ctx.InvokeAsync(plugin, admin, PkiMethods.Revoke, PkiJson.Serialize(new RevokeRequest([mine.SerialNumber], Oadm.Sdk.Certificates.RevocationReason.Other)));
        Assert.True(authority.IsRevoked(mine.SerialNumber));
        Assert.Equal("admin", authority.FindRevocation(mine.SerialNumber)!.UserName);
        Assert.Contains(ctx.EventsSeenBy(group), e => e.Topic == PkiMethods.CertificatesTopic);
        Assert.Contains(ctx.EventsSeenBy(admin), e => e.Topic == PkiMethods.CertificatesTopic);
    }

    [Fact]
    public void Names_of_a_device_a_group_user_added_are_only_its_ip_addresses_and_its_axis_name()
    {
        var names = CertificateNames.For("cam.example.com", "B8A44F631339", "cam-entrance", "cam-entrance.example.com", ["10.0.0.48"], trustNames: false);

        Assert.Equal("10.0.0.48", names.CommonName);
        Assert.Equal(["10.0.0.48"], names.IpAddresses);
        Assert.Equal(["axis-b8a44f631339.local"], names.DnsNames);
        Assert.Equal("axis-b8a44f631339.local", CertificateNames.For("cam.example.com", "B8A44F631339", null, null, [], trustNames: false).CommonName);
        Assert.Equal(["cam.example.com", "cam-entrance", "cam-entrance.example.com", "axis-b8a44f631339.local"],
            CertificateNames.For("cam.example.com", "B8A44F631339", "cam-entrance", "cam-entrance.example.com", ["10.0.0.48"], trustNames: true).DnsNames);
    }

    /// <summary>
    /// Enable HTTPS and what the camera reports: a user of a group could set it on the camera (Network settings, VAPIX
    /// Commander, Web View). A device a group user added gets no host name or domain; a device a group covers gets only
    /// the address it was added with; a device no group covers keeps everything the camera reports.
    /// </summary>
    [Theory]
    [InlineData("added by a group user", "10.0.0.48,10.0.0.99", "axis-b8a44f631339.local")]
    [InlineData("in a group", "10.0.0.48", "axis-b8a44f631339.local")]
    [InlineData("in no group", "10.0.0.48,10.0.0.99", "bank,bank.example.com,axis-b8a44f631339.local")]
    public async Task Enable_https_takes_from_the_camera_only_what_no_group_user_could_have_set(string device, string addresses, string names)
    {
        await using var h = await TaskHarness.StartAsync();
        var system = h.Camera.NetworkInfo["data"]!["system"]!.AsObject();
        system["hostname"]!["hostname"] = "bank";
        system["resolver"]!["domainName"] = "example.com";
        h.Camera.NetworkInfo["data"]!["devices"]![0]!["IPv4"]!["addresses"]!.AsArray().Add(new JsonObject { ["address"] = "10.0.0.99", ["scope"] = "global" });
        switch (device)
        {
            case "added by a group user":
                h.Device = h.Device with { AddedByAllDevices = false };
                break;
            case "in a group":
                h.Pki.Scopes.Add("Building B", [h.Device.Id]);
                break;
            default:
                h.Pki.Scopes.Add("Lobby", [Guid.NewGuid()]);
                break;
        }

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));

        using var served = h.Served(h.Camera.WebServerAlias);
        var san = served.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal("CN=10.0.0.48", served.Subject);
        Assert.Equal(addresses.Split(',').Select(IPAddress.Parse), san.EnumerateIPAddresses());
        Assert.Equal(names.Split(','), san.EnumerateDnsNames());
    }

    [Fact]
    public void Names_of_a_device_a_group_covers_are_the_address_it_was_added_with_and_its_axis_name()
    {
        var added = new PkiFakeDevice(Guid.NewGuid(), "cam.example.com", "B8A44F631339");
        var network = new DeviceNetworkInfo("bank", "example.com", ["10.0.0.99"], null);

        var covered = CertificateDeployment.NamesFor(added, network, inAnyGroup: true);
        Assert.Equal("cam.example.com", covered.CommonName);
        Assert.Empty(covered.IpAddresses);
        Assert.Equal(["cam.example.com", "axis-b8a44f631339.local"], covered.DnsNames);
        var free = CertificateDeployment.NamesFor(added, network, inAnyGroup: false);
        Assert.Equal(["cam.example.com", "bank", "bank.example.com", "axis-b8a44f631339.local"], free.DnsNames);
        Assert.Equal(["10.0.0.99"], free.IpAddresses);
    }

    [Fact]
    public async Task The_page_of_a_group_user_hides_every_action_on_the_ca()
    {
        await using var h = await PkiHarness.StartAsync();
        using var vm = new PkiViewModel(new PluginPageContext(h.Plugin, h.Hub) { HasAllDevices = false }, new FakeTrustStore());
        await vm.LoadAsync();

        Assert.False(vm.MayManage);
        Assert.False(vm.MayTrustServer);
        Assert.False(vm.Policies.IsShown);
    }
}
