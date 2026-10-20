using System.Net;
using System.Net.Sockets;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;

namespace Oadm.SiteNode.Networks;

/// <summary>
/// Which networks the node reaches (docs/sites.md, "Reach"): all cards (default) or the cards chosen in the app, and extra
/// routed subnets. Every address the server asks for is checked here;
/// the server cannot widen it. Never reached: special addresses (<see cref="Subnet.IsUsable"/>: loopback, link-local,
/// multicast, 0.0.0.0/8, broadcast) and the node's own addresses on any card (its own services are not the devices').
/// </summary>
/// <param name="allowLoopback">Tests only: a fake camera network on loopback (127.0.0.0/8) counts like a card's network.</param>
public sealed class NetworkScope(NodeStore store, INetworkCards cards, bool allowLoopback = false)
{
    private readonly Lock _lock = new();
    private string? _cardsSignature;

    /// <summary>Raised by <see cref="CheckCards"/> when a card, its addresses or its default router changed.</summary>
    public event Action? CardsChanged;

    /// <summary>Reads the cards again and raises <see cref="CardsChanged"/> when they differ from the last check (the first check only remembers them).</summary>
    public bool CheckCards()
    {
        var signature = string.Join('|', cards.List().OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => string.Join(';', c.Id, c.Name, c.HasDefaultRouter, string.Join(',', c.Addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal)))));
        bool changed;
        lock (_lock)
        {
            changed = _cardsSignature is not null && !string.Equals(_cardsSignature, signature, StringComparison.Ordinal);
            _cardsSignature = signature;
        }

        if (changed)
            NodeEvents.Raise(CardsChanged);
        return changed;
    }

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
        var loopback = allowLoopback && IPAddress.IsLoopback(address);
        if ((!Subnet.IsUsable(address) && !loopback) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.Broadcast.Equals(address))
            return false;
        var list = cards.List();
        if (!loopback && list.Any(card => card.Addresses.Any(a => a.Address.Equals(address))))
            return false; // the node itself
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

    public static bool IsReached(NodeConfig config, NetworkCard card)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(card);
        return ReachesAllCards(config) || (config.Cards.TryGetValue(card.Id, out var chosen) && chosen);
    }

    /// <summary>All cards: the default, and also when no card is chosen (a choice of none would reach nothing).</summary>
    public static bool ReachesAllCards(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AllCards || !config.Cards.Any(e => e.Value);
    }

    private static IEnumerable<Subnet> ExtraSubnets(NodeConfig config) =>
        config.ExtraSubnets.Select(Subnet.Parse).Where(s => s is not null).Select(s => s!.Value);
}
