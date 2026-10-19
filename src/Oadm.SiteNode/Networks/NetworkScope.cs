using System.Net;
using System.Net.Sockets;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;

namespace Oadm.SiteNode.Networks;

/// <summary>
/// Which networks the node reaches (docs/sites.md, "Reach"): the cards chosen in the app (default every card except the
/// one with the default router), all cards, and extra routed subnets. Every address the server asks for is checked here;
/// the server cannot widen it.
/// </summary>
public sealed class NetworkScope(NodeStore store, INetworkCards cards)
{
    /// <summary>The cards with "Reach devices on this network".</summary>
    public IReadOnlyList<CardView> Cards()
    {
        var config = store.Snapshot();
        return [.. cards.List().Select(c => new CardView(c.Id, c.Name, [.. c.Addresses.Select(a => a.ToString())], IsReached(config, c), c.HasDefaultRouter))];
    }

    /// <summary>True when the address is on a reached card's network or in an extra subnet.</summary>
    public bool Reaches(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (!Subnet.IsUsable(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.Broadcast.Equals(address))
            return false;
        var list = cards.List();
        return store.Read(config =>
            list.Any(card => IsReached(config, card) && card.Addresses.Any(a => a.Subnet.Contains(address)))
            || ExtraSubnets(config).Any(s => s.Contains(address)));
    }

    /// <summary>The node's own address towards a device: the reached card on its network, else the route the OS would take to an extra subnet.</summary>
    public IPAddress? LocalAddressFor(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var config = store.Snapshot();
        foreach (var card in cards.List())
        {
            if (!IsReached(config, card))
                continue;
            var match = card.Addresses.FirstOrDefault(a => a.Subnet.Contains(address));
            if (match is not null)
                return match.Address;
        }

        if (!ExtraSubnets(config).Any(s => s.Contains(address)))
            return null;
        try
        {
            // A connected UDP socket sends nothing; the OS only picks the source address of its route.
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(address, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>The addresses of the reached cards (relay listeners and mDNS browse bind to them).</summary>
    public IReadOnlyList<CardAddress> ReachedAddresses()
    {
        var config = store.Snapshot();
        return [.. cards.List().Where(c => IsReached(config, c)).SelectMany(c => c.Addresses)];
    }

    /// <summary>Every card address (the announcement of a waiting node goes out on all cards: the server is usually on the office side).</summary>
    public IReadOnlyList<CardAddress> AllAddresses() => [.. cards.List().SelectMany(c => c.Addresses)];

    public static bool IsReached(NodeConfig config, NetworkCard card)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(card);
        if (config.AllCards)
            return true;
        return config.Cards.TryGetValue(card.Id, out var reached) ? reached : !card.HasDefaultRouter;
    }

    private static IEnumerable<Subnet> ExtraSubnets(NodeConfig config) =>
        config.ExtraSubnets.Select(Subnet.Parse).Where(s => s is not null).Select(s => s!.Value);
}
