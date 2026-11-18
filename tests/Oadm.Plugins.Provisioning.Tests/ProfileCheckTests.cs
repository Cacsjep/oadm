using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The profile check: one error per node, the fixed nodes first, the same in the editor and before activation.</summary>
public sealed class ProfileCheckTests
{
    private static readonly Harness H = new();

    [Fact]
    public void A_new_profile_needs_only_its_password()
    {
        var profile = ProfileStore.NewProfile("Lobby");
        var check = ProfileCheck.Check(profile, NodeRegistry.Default, ProfileCheckContext.Empty);

        Assert.Equal([NodeKind.SetPassword, NodeKind.AddToOadm], profile.Nodes.Select(n => n.Kind));
        Assert.False(profile.Nodes[0].ContinueOnError);
        Assert.Empty(check.ProfileErrors);
        Assert.Equal(new Dictionary<string, string> { ["set-password"] = SetPasswordNode.ChoosePassword }, check.NodeErrors);
    }

    [Fact]
    public void Each_node_gets_one_error_its_place_first()
    {
        var profile = H.Profile(
            Harness.Node("ip", NodeKind.SetIp, new SetIpSettings("x", "y", "z", "w")),
            Harness.Node("time", NodeKind.DateTime, new DateTimeSettings()),
            Harness.Node("again", NodeKind.SetPassword, new SetPasswordSettings(H.AutoAdd.Root)),
            Harness.Node("ok", NodeKind.WaitForApproval));

        var check = ProfileCheck.Check(profile, NodeRegistry.Default, ProfileCheckContext.Empty);

        Assert.Equal("Enter the first address of the pool, e.g. 192.168.15.10.", check.NodeErrors["ip"]);
        Assert.Equal("Choose a time zone.", check.NodeErrors["time"]);
        Assert.Equal("Set password is always the first node.", check.NodeErrors["again"]);
        Assert.False(check.NodeErrors.ContainsKey("ok"));
        Assert.Equal(3, check.NodeErrors.Count);
        Assert.False(check.IsValid);
        Assert.Equal("Set IP: Enter the first address of the pool, e.g. 192.168.15.10.", ProfileCheck.FirstError(check, profile));
    }

    [Fact]
    public void The_fixed_nodes_come_first_and_ids_are_unique()
    {
        var swapped = H.Profile() with { Nodes = [.. H.Profile().Nodes.Reverse()] };
        var check = ProfileCheck.Check(swapped, NodeRegistry.Default, ProfileCheckContext.Empty);
        Assert.Contains("A profile starts with Set password and Add to OADM.", check.ProfileErrors);
        Assert.Equal("Set password is always the first node.", check.NodeErrors["set-password"]);

        var twice = H.Profile(Harness.Node("add-to-oadm", NodeKind.WaitForApproval));
        Assert.Contains("Node 3 needs an id of its own.", ProfileCheck.Check(twice, NodeRegistry.Default, ProfileCheckContext.Empty).ProfileErrors);

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
        var profile = H.Profile() with { Nodes = [.. H.Profile().Nodes.Select(n => n with { ContinueOnError = true })] };

        var stored = ProfileStore.Normalize(profile, NodeRegistry.Default);

        Assert.False(stored.Nodes[0].ContinueOnError);
        Assert.True(stored.Nodes[1].ContinueOnError);
    }
}
