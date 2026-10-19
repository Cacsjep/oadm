using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Oadm.SiteNode.Networks;

/// <summary>One address of a card with its prefix ("192.168.0.5/24").</summary>
public sealed record CardAddress(IPAddress Address, int PrefixLength)
{
    public Subnet Subnet => Subnet.Of(Address, PrefixLength);

    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>A network card of the node's machine.</summary>
public sealed record NetworkCard(string Id, string Name, IReadOnlyList<CardAddress> Addresses, bool HasDefaultRouter);

/// <summary>The machine's network cards (a fake list in tests).</summary>
public interface INetworkCards
{
    IReadOnlyList<NetworkCard> List();
}

/// <summary>
/// The OS's cards that are up, without loopback; IPv4 and global / unique-local IPv6 addresses (link-local addresses
/// with a zone id are not used in v1). Read again at most every 10 seconds.
/// </summary>
public sealed class SystemNetworkCards(TimeProvider time) : INetworkCards
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    private readonly Lock _lock = new();
    private IReadOnlyList<NetworkCard> _cards = [];
    private long _readAt;
    private bool _read;

    public IReadOnlyList<NetworkCard> List()
    {
        lock (_lock)
        {
            if (_read && time.GetElapsedTime(_readAt) < MaxAge)
                return _cards;
            _cards = Read();
            _readAt = time.GetTimestamp();
            _read = true;
            return _cards;
        }
    }

    private static List<NetworkCard> Read()
    {
        var cards = new List<NetworkCard>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var addresses = new List<CardAddress>();
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (IPAddress.IsLoopback(address) || !Subnet.IsUsable(address))
                    continue;
                var prefix = unicast.PrefixLength;
                if (prefix <= 0)
                    prefix = address.AddressFamily == AddressFamily.InterNetwork ? 24 : 64;
                addresses.Add(new CardAddress(address, prefix));
            }

            if (addresses.Count == 0)
                continue;
            var router = properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            cards.Add(new NetworkCard(nic.Id, nic.Name, addresses, router));
        }

        return cards;
    }
}
