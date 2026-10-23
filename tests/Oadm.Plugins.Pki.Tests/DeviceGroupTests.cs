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
        PkiMethods.RemovePrevious, PkiMethods.Backup, PkiMethods.InstallServerTrust, PkiMethods.SaveSettings, PkiMethods.ImportRadiusCa,
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
            .WithoutDevices(PkiMethods.GetState, "the CA and the settings; the counts of issued certificates only of the user's devices");
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

        // A change of the settings: the group's page gets the state with its own counts, users with all devices the full one.
        var config = PkiJson.Deserialize<PkiState>(await ctx.InvokeAsync(plugin, admin, PkiMethods.GetState, null)).Config with { ExpiryWarningDays = 45 };
        await ctx.InvokeAsync(plugin, admin, PkiMethods.SaveSettings, PkiJson.Serialize(new SaveSettingsRequest(config)));
        PkiState Last(ICallContext watcher) => PkiJson.Deserialize<PkiState>(ctx.EventsSeenBy(watcher).Last(e => e.Topic == PkiMethods.StateTopic).PayloadJson);
        Assert.Equal((1, 45), (Last(group).IssuedDevices, Last(group).Config.ExpiryWarningDays));
        Assert.Equal(2, Last(admin).IssuedDevices);
        Assert.DoesNotContain(ctx.EventsSeenBy(ctx.CallAsGroup([outside.Id])), e => e.Topic == PkiMethods.StateTopic); // never read the page
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
        Assert.False(vm.SaveCommand.CanExecute(null));
    }
}
