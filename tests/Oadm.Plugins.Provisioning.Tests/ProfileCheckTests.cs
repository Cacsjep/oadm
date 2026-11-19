using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The profile check: one error per node, Set password first, the same in the editor and before activation; older profiles converted.</summary>
public sealed class ProfileCheckTests
{
    private static readonly Harness H = new();

    [Fact]
    public void A_new_profile_is_set_password_alone_and_needs_only_its_password()
    {
        var profile = ProfileStore.NewProfile("Lobby");
        var check = ProfileCheck.Check(profile, NodeRegistry.Default, ProfileCheckContext.Empty);

        Assert.Equal([NodeKind.SetPassword], profile.Nodes.Select(n => n.Kind));
        Assert.False(profile.Nodes[0].ContinueOnError);
        Assert.Empty(check.ProfileErrors);
        Assert.Equal(new Dictionary<string, string> { ["set-password"] = SetPasswordNode.ChoosePassword }, check.NodeErrors);
    }

    [Fact]
    public void Each_node_gets_one_error_its_place_first()
    {
        var profile = H.Profile(
            Harness.Node("ip", NodeKind.Network, new NetworkNodeSettings(IpMode.Pool, "x", "y", "z", "w")),
            Harness.Node("time", NodeKind.DateTime, new DateTimeSettings()),
            Harness.Node("again", NodeKind.SetPassword, new SetPasswordSettings(H.AutoAdd.Root)),
            Harness.Node("ok", NodeKind.Autofocus));

        var check = ProfileCheck.Check(profile, NodeRegistry.Default, ProfileCheckContext.Empty);

        Assert.Equal("Enter the first address of the pool, e.g. 192.168.15.10.", check.NodeErrors["ip"]);
        Assert.Equal("Choose a time zone.", check.NodeErrors["time"]);
        Assert.Equal("Set password is always the first node.", check.NodeErrors["again"]);
        Assert.False(check.NodeErrors.ContainsKey("ok"));
        Assert.Equal(3, check.NodeErrors.Count);
        Assert.False(check.IsValid);
        Assert.Equal("Network: Enter the first address of the pool, e.g. 192.168.15.10.", ProfileCheck.FirstError(check, profile));
    }

    [Fact]
    public void Set_password_comes_first_and_ids_are_unique()
    {
        var swapped = H.Profile(Harness.Node("af", NodeKind.Autofocus)) with { Nodes = [.. H.Profile(Harness.Node("af", NodeKind.Autofocus)).Nodes.Reverse()] };
        var check = ProfileCheck.Check(swapped, NodeRegistry.Default, ProfileCheckContext.Empty);
        Assert.Contains(ProfileCheck.StartsWithSetPassword, check.ProfileErrors);
        Assert.Equal("Set password is always the first node.", check.NodeErrors["set-password"]);
        Assert.Equal("Set password comes first.", check.NodeErrors["af"]);

        var twice = H.Profile(Harness.Node("set-password", NodeKind.Autofocus));
        Assert.Contains("Node 2 needs an id of its own.", ProfileCheck.Check(twice, NodeRegistry.Default, ProfileCheckContext.Empty).ProfileErrors);

        var unknown = H.Profile(Harness.Node("x", (NodeKind)99));
        Assert.Equal("This OADM version does not know this node.", ProfileCheck.Check(unknown, NodeRegistry.Default, ProfileCheckContext.Empty).NodeErrors["x"]);
    }

    [Fact]
    public void The_name_is_needed_and_unique()
    {
        var other = H.Profile() with { Id = Guid.NewGuid(), Name = "lobby" };
        Assert.Equal(["A profile named Lobby exists already."], ProfileCheck.Check(H.Profile(), NodeRegistry.Default, ProfileCheckContext.Empty, [other]).ProfileErrors);
        Assert.Equal(["Enter a name."], ProfileCheck.Check(H.Profile() with { Name = " " }, NodeRegistry.Default, ProfileCheckContext.Empty).ProfileErrors);
    }

    [Fact]
    public void Set_password_always_stops_whatever_the_profile_says()
    {
        var profile = H.Profile(Harness.Node("af", NodeKind.Autofocus));
        profile = profile with { Nodes = [.. profile.Nodes.Select(n => n with { ContinueOnError = true })] };

        var stored = ProfileStore.Normalize(profile, NodeRegistry.Default);

        Assert.False(stored.Nodes[0].ContinueOnError);
        Assert.True(stored.Nodes[1].ContinueOnError);
    }

    // ---- profiles of the build of 2026-11-18 ----

    /// <summary>A stored profile of the build of 2026-11-18, as its JSON was written.</summary>
    private const string OldBook = """
        {"profiles":[{"id":"6f1c2a52-6a8e-4c4b-9a55-0d5b0f3f0a01","name":"Lobby","nodes":[
          {"id":"set-password","kind":"setPassword","continueOnError":false,"settings":{"credentialId":"7b2b3c55-0000-4000-8000-000000000001"}},
          {"id":"add-to-oadm","kind":"addToOadm","continueOnError":true,"settings":{"tags":["Lobby"]}},
          {"id":"n3","kind":"setIp","continueOnError":false,"settings":{"first":"192.168.15.10","last":"192.168.15.50","subnetMask":"255.255.255.0","gateway":"192.168.15.1","dnsServers":["192.168.15.1"]}},
          {"id":"n4","kind":"waitForApproval","continueOnError":true},
          {"id":"n5","kind":"dateTime","continueOnError":true,"settings":{"timeZone":"Europe/Vienna"}},
          {"id":"n6","kind":"networkSettings","continueOnError":true,"settings":{"dnsServers":["10.0.0.1","10.0.0.2"],"searchDomain":"site.local","hostName":"{model}-{n}"}}
        ]}],"activeId":"6f1c2a52-6a8e-4c4b-9a55-0d5b0f3f0a01"}
        """;

    [Fact]
    public async Task Older_profiles_load_with_add_to_oadm_and_wait_for_approval_dropped_and_one_network_node()
    {
        var settings = new FakeSettings();
        settings.Values[ProfileStore.ProfilesKey] = OldBook;
        using var store = new ProfileStore(settings);

        await store.LoadAsync(CancellationToken.None);

        var profile = Assert.Single(store.Book.Profiles);
        Assert.Equal([NodeKind.SetPassword, NodeKind.Network, NodeKind.DateTime], profile.Nodes.Select(n => n.Kind));
        var network = profile.Nodes[1];
        Assert.Equal("n3", network.Id); // the place and the id of the first of the two
        Assert.False(network.ContinueOnError);
        var merged = NodeSettings.Read<NetworkNodeSettings>(network.Settings)!;
        Assert.Equal(IpMode.Pool, merged.Ip);
        Assert.Equal(("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.1"), (merged.First, merged.Last, merged.SubnetMask, merged.Gateway));
        Assert.Equal(["10.0.0.1", "10.0.0.2"], merged.DnsServers!); // Network settings' servers win
        Assert.Equal("site.local", merged.SearchDomain);
        Assert.Null(new NetworkNode().Check(network.Settings, ProfileCheckContext.Empty));
        Assert.Equal(profile.Id, store.Book.ActiveId);

        // Stored again converted: no old kind left.
        Assert.DoesNotContain("addToOadm", settings.Values[ProfileStore.ProfilesKey], StringComparison.Ordinal);
        Assert.DoesNotContain("setIp", settings.Values[ProfileStore.ProfilesKey], StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_set_ip_alone_becomes_a_pool_with_its_dns_and_network_settings_alone_keeps_the_address()
    {
        var onlySetIp = H.Profile(Harness.Node("ip", NodeKind.SetIp, new SetIpSettings("192.168.15.10", "192.168.15.20", "255.255.255.0", "192.168.15.1", ["192.168.15.1"])));
        var converted = ProfileUpgrade.Convert(onlySetIp, out var changed);
        Assert.True(changed);
        var pool = NodeSettings.Read<NetworkNodeSettings>(converted.Nodes[1].Settings)!;
        Assert.Equal((IpMode.Pool, "192.168.15.10"), (pool.Ip, pool.First));
        Assert.Equal(["192.168.15.1"], pool.DnsServers!);

        var onlyDns = H.Profile(Harness.Node("dns", NodeKind.NetworkSettings, new NetworkSettingsSettings(["10.0.0.1"], null)));
        var keep = NodeSettings.Read<NetworkNodeSettings>(ProfileUpgrade.Convert(onlyDns, out _).Nodes[1].Settings)!;
        Assert.Equal(IpMode.Keep, keep.Ip);
        Assert.Equal(["10.0.0.1"], keep.DnsServers!);

        var today = H.Profile(Harness.Node("af", NodeKind.Autofocus));
        Assert.Same(today, ProfileUpgrade.Convert(today, out var unchanged));
        Assert.False(unchanged);

        // A save (the editor, an agent) with an old kind is converted too.
        var saved = ProfileStore.Normalize(H.Profile(Harness.Node("old", NodeKind.WaitForApproval)), NodeRegistry.Default);
        Assert.Equal([NodeKind.SetPassword], saved.Nodes.Select(n => n.Kind));
        Assert.Equal(JsonValueKind.Object, NodeSettings.Write(keep).ValueKind);
    }
}
