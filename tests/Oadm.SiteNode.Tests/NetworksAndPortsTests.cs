using System.Net;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>Reached networks and both port lists (CLAUDE.md "# Sites", "Reach", "Ports").</summary>
public sealed class NetworksAndPortsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly NodeStore _store;
    private readonly FakeCards _cards = new(
        new NetworkCard("cam", "Cameras", [new CardAddress(IPAddress.Parse("192.168.0.5"), 24), new CardAddress(IPAddress.Parse("fd00::5"), 64)], HasDefaultRouter: false),
        new NetworkCard("office", "Office", [new CardAddress(IPAddress.Parse("10.0.0.7"), 24)], HasDefaultRouter: true));

    private readonly NetworkScope _scope;

    public NetworksAndPortsTests()
    {
        _store = new NodeStore(_folder.Path);
        _scope = new NetworkScope(_store, _cards);
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void EveryCardIsReachedByDefault()
    {
        Assert.True(_store.Read(c => c.AllCards));
        Assert.True(_scope.Reaches(IPAddress.Parse("192.168.0.20")));
        Assert.True(_scope.Reaches(IPAddress.Parse("fd00::20")));
        Assert.True(_scope.Reaches(IPAddress.Parse("10.0.0.20"))); // the card with the default router too
        Assert.False(_scope.Reaches(IPAddress.Parse("192.168.1.20")));
        Assert.False(_scope.Reaches(IPAddress.Parse("8.8.8.8")));
        Assert.Contains(_scope.Cards(), c => c.Id == "office" && c.Reached && c.HasDefaultRouter);
        Assert.Contains(_scope.Cards(), c => c.Id == "cam" && c.Reached && c.Addresses.Contains("192.168.0.5/24"));
    }

    [Fact]
    public void ChosenCardsRestrictTheReachAndExtraSubnetsWidenIt()
    {
        // The machine also sits in the office network: only the camera card is chosen.
        _store.Update(c =>
        {
            c.AllCards = false;
            c.Cards["cam"] = true;
        });
        Assert.True(_scope.Reaches(IPAddress.Parse("192.168.0.20")));
        Assert.False(_scope.Reaches(IPAddress.Parse("10.0.0.20")));
        Assert.Contains(_scope.Cards(), c => c.Id == "office" && !c.Reached);

        // No chosen card is all cards again (a choice of none would reach nothing); false entries of older versions count as none.
        _store.Update(c => c.Cards["cam"] = false);
        Assert.True(NetworkScope.ReachesAllCards(_store.Snapshot()));
        Assert.True(_scope.Reaches(IPAddress.Parse("10.0.0.20")));

        Assert.False(_scope.Reaches(IPAddress.Parse("10.20.5.9")));
        _store.Update(c => c.ExtraSubnets.Add("10.20.0.0/16"));
        Assert.True(_scope.Reaches(IPAddress.Parse("10.20.5.9")));
    }

    [Fact]
    public void LinkLocalAndSpecialAddressesAreNeverReached()
    {
        _store.Update(c => c.AllCards = true);
        Assert.False(_scope.Reaches(IPAddress.Parse("fe80::1%3")));
        Assert.False(_scope.Reaches(IPAddress.Parse("fe80::1")));
        Assert.False(_scope.Reaches(IPAddress.Any));
        Assert.False(_scope.Reaches(IPAddress.Broadcast));
        Assert.True(_scope.Reaches(IPAddress.Parse("::ffff:192.168.0.9")));
    }

    [Fact]
    public void TheNodesOwnAddressesAndSpecialAddressesAreNeverReached()
    {
        _store.Update(c =>
        {
            c.AllCards = true;
            c.ExtraSubnets.Add("169.0.0.0/8"); // wide enough to hold link-local; still never reached
        });

        Assert.False(_scope.Reaches(IPAddress.Parse("192.168.0.5")));
        Assert.False(_scope.Reaches(IPAddress.Parse("fd00::5")));
        Assert.False(_scope.Reaches(IPAddress.Parse("10.0.0.7")));
        Assert.False(_scope.Reaches(IPAddress.Parse("::ffff:192.168.0.5")));
        Assert.False(_scope.Reaches(IPAddress.Loopback));
        Assert.False(_scope.Reaches(IPAddress.Parse("169.254.10.20")));
        Assert.False(_scope.Reaches(IPAddress.Parse("224.0.0.251")));
        Assert.True(_scope.Reaches(IPAddress.Parse("169.1.2.3")));
        Assert.True(_scope.Reaches(IPAddress.Parse("192.168.0.6")));
        Assert.False(new NetworkScope(_store, FakeCards.LoopbackSite()).Reaches(IPAddress.Parse("127.0.0.9")));
        Assert.True(new NetworkScope(_store, FakeCards.LoopbackSite(), allowLoopback: true).Reaches(IPAddress.Parse("127.0.0.9")));
    }

    [Fact]
    public void ACardChangeIsNoticedOnce()
    {
        var raised = 0;
        _scope.CardsChanged += () => raised++;

        Assert.False(_scope.CheckCards()); // the first look only remembers
        Assert.False(_scope.CheckCards());
        _cards.Replace(0, _cards.List()[0] with { Addresses = [new CardAddress(IPAddress.Parse("192.168.0.77"), 24)] });
        Assert.True(_scope.CheckCards());
        Assert.False(_scope.CheckCards());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheLocalAddressTowardsADeviceIsTheReachedCardOnItsNetwork()
    {
        Assert.Equal(IPAddress.Parse("192.168.0.5"), _scope.LocalAddressFor(IPAddress.Parse("192.168.0.30")));
        Assert.Equal(IPAddress.Parse("10.0.0.7"), _scope.LocalAddressFor(IPAddress.Parse("10.0.0.30"))); // all cards by default
        _store.Update(FakeCards.ChooseCameraCard);
        Assert.Null(_scope.LocalAddressFor(IPAddress.Parse("10.0.0.30")));
    }

    [Theory]
    [InlineData("10.20.0.0/16", "10.20.0.0/16")]
    [InlineData("10.20.5.9/16", "10.20.0.0/16")]
    [InlineData("192.168.0.0/24", "192.168.0.0/24")]
    [InlineData("fd00:1::/48", "fd00:1::/48")]
    [InlineData("10.20.0.0", null)]
    [InlineData("10.20/16", null)]
    [InlineData("10.20.0.0/33", null)]
    [InlineData("10.20.0.0/0", null)]
    [InlineData("fe80::/64", null)]
    [InlineData("camera/24", null)]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("10.0.0.0/7", null)]
    [InlineData("0.0.0.0/1", null)]
    [InlineData("0.0.0.0/8", null)]
    [InlineData("127.0.0.0/8", null)]
    [InlineData("169.254.0.0/16", null)]
    [InlineData("224.0.0.0/8", null)]
    [InlineData("239.1.0.0/16", null)]
    [InlineData("255.255.255.255/32", null)]
    [InlineData("::1/128", null)]
    [InlineData("ff02::/16", null)]
    [InlineData("fd00::/8", null)]
    public void SubnetsAreParsedAndNormalized(string text, string? expected) =>
        Assert.Equal(expected, Subnet.Parse(text)?.ToString());

    [Fact]
    public void ConnectToHasTheDefaultsAndListenOnIsEmpty()
    {
        var ports = new PortPolicy(_store);
        Assert.True(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 80));
        Assert.True(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 443));
        Assert.True(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 554));
        Assert.False(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 8443));
        Assert.False(ports.Allows(PortUse.Listen, PortProtocol.Tcp, 80));
        Assert.False(ports.Allows(PortUse.Listen, PortProtocol.Udp, 123));
    }

    [Fact]
    public void RequestedPortsWaitUntilAllowedOrDismissed()
    {
        var ports = new PortPolicy(_store);

        Assert.Equal(2, ports.Request([
            new PortEntry(PortUse.Connect, PortProtocol.Tcp, 8443, "camera 192.168.0.30"),
            new PortEntry(PortUse.Listen, PortProtocol.Udp, 123, "NTP server"),
            new PortEntry(PortUse.Connect, PortProtocol.Tcp, 80, "already allowed"),
            new PortEntry(PortUse.Connect, PortProtocol.Tcp, 70000, "invalid"),
        ]));
        Assert.Equal(0, ports.Request([new PortEntry(PortUse.Connect, PortProtocol.Tcp, 8443, "again")]));
        Assert.False(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 8443));

        ports.Allow(new PortEntry(PortUse.Connect, PortProtocol.Tcp, 8443));
        Assert.True(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 8443));
        Assert.Equal("camera 192.168.0.30", _store.Read(c => c.Allowed.Single(p => p.Port == 8443).Reason));
        Assert.False(ports.Allows(PortUse.Listen, PortProtocol.Tcp, 8443)); // Connect to never allows a listener

        ports.Dismiss(new PortEntry(PortUse.Listen, PortProtocol.Udp, 123));
        Assert.Empty(_store.Read(c => c.Requested));
        Assert.False(ports.Allows(PortUse.Listen, PortProtocol.Udp, 123));

        ports.Remove(new PortEntry(PortUse.Connect, PortProtocol.Tcp, 554));
        Assert.False(ports.Allows(PortUse.Connect, PortProtocol.Tcp, 554));
    }

    [Fact]
    public void AServerCannotFillTheRequestList()
    {
        var ports = new PortPolicy(_store);
        ports.Request(Enumerable.Range(1000, 1000).Select(p => new PortEntry(PortUse.Connect, PortProtocol.Tcp, p, "x")));
        Assert.Equal(PortPolicy.MaxRequests, _store.Read(c => c.Requested.Count));
    }

    [Fact]
    public void NodeJsonKeepsTheChoicesAcrossARestart()
    {
        _store.Update(c =>
        {
            c.AllCards = false;
            c.Cards["office"] = true;
            c.ExtraSubnets.Add("10.20.0.0/16");
            c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Udp, 123, "NTP server"));
        });

        var reloaded = new NodeStore(_folder.Path).Snapshot();

        Assert.True(reloaded.Cards["office"]);
        Assert.False(reloaded.AllCards);
        Assert.Equal(["10.20.0.0/16"], reloaded.ExtraSubnets);
        Assert.Contains(reloaded.Allowed, p => p is { Use: PortUse.Listen, Protocol: PortProtocol.Udp, Port: 123 });
        Assert.Equal(_store.Read(c => c.Id), reloaded.Id);
        Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(_folder.Path, NodeStore.FileName)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableNodeJsonStartsWithDefaultsAndKeepsACopy()
    {
        File.WriteAllText(Path.Combine(_folder.Path, NodeStore.FileName), "{ not json");

        var store = new NodeStore(_folder.Path);

        Assert.NotEmpty(store.Read(c => c.Id));
        Assert.True(File.Exists(Path.Combine(_folder.Path, NodeStore.FileName + ".unreadable")));
    }
}
