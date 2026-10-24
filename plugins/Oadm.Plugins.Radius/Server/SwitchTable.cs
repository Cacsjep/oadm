using System.Collections.Frozen;
using System.Net;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Sessions;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// One configured switch as the server answers it: its name, address, site, the addresses its requests come from (the
/// address itself, or what its host name resolved to) and the <see cref="RadiusClient"/> (key and decrypted secret).
/// </summary>
public sealed class SwitchEntry
{
    public SwitchEntry(Guid id, string name, string address, Guid? siteId, ReadOnlyMemory<byte> secret, IReadOnlyList<IPAddress> addresses)
    {
        Id = id;
        Name = name;
        Address = address;
        SiteId = siteId;
        Addresses = [.. addresses.Select(RadiusRules.Normalize).Distinct()];
        Client = new RadiusClient(KeyOf(id), secret);
    }

    public Guid Id { get; }

    public string Name { get; }

    /// <summary>The IP address or host name the user entered.</summary>
    public string Address { get; }

    /// <summary>Null = this server's network.</summary>
    public Guid? SiteId { get; }

    /// <summary>Where its requests come from (as this server or the site node sees them).</summary>
    public IReadOnlyList<IPAddress> Addresses { get; }

    /// <summary>Key (the id) and secret for the protocol layer. Never logged.</summary>
    public RadiusClient Client { get; }

    /// <summary>The same switch with other source addresses (its host name resolved again).</summary>
    public SwitchEntry WithAddresses(IReadOnlyList<IPAddress> addresses) => new(Id, Name, Address, SiteId, Client.Secret, addresses);

    /// <summary>The protocol key of a switch: its id ("N" format).</summary>
    public static string KeyOf(Guid id) => id.ToString("N");

    public override string ToString() => Name;
}

/// <summary>
/// The switches as one immutable snapshot (docs/spec/radius-server.md "Clients"): every datagram looks its switch up here
/// by site + source address; a change builds a new table and replaces the old one atomically, so the switches change while
/// the server runs without a lock on the hot path.
/// </summary>
public sealed class SwitchTable
{
    private readonly FrozenDictionary<(Guid Site, IPAddress Address), SwitchEntry> _byAddress;
    private readonly FrozenDictionary<string, SwitchEntry> _byKey;

    public SwitchTable(IEnumerable<SwitchEntry> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        var list = switches.ToList();
        All = list;
        ById = list.ToFrozenDictionary(s => s.Id);
        _byKey = list.ToFrozenDictionary(s => s.Client.Key, StringComparer.Ordinal);
        var byAddress = new Dictionary<(Guid, IPAddress), SwitchEntry>();
        foreach (var entry in list)
        {
            foreach (var address in entry.Addresses)
            {
                byAddress.TryAdd((entry.SiteId ?? Guid.Empty, address), entry); // the first switch wins a shared address
            }
        }

        _byAddress = byAddress.ToFrozenDictionary();
    }

    public static SwitchTable Empty { get; } = new([]);

    /// <summary>The switches in the order they were added.</summary>
    public IReadOnlyList<SwitchEntry> All { get; }

    public FrozenDictionary<Guid, SwitchEntry> ById { get; }

    /// <summary>Sites that have at least one switch (each gets a relay).</summary>
    public IReadOnlySet<Guid> Sites => All.Select(s => s.SiteId).OfType<Guid>().ToHashSet();

    /// <summary>The switch a datagram from <paramref name="source"/> at <paramref name="siteId"/> belongs to; null for an unknown sender.</summary>
    public SwitchEntry? Find(Guid? siteId, IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _byAddress.GetValueOrDefault((siteId ?? Guid.Empty, RadiusRules.Normalize(source)));
    }

    /// <summary>The switch of a protocol key (<see cref="RadiusClient.Key"/>).</summary>
    public SwitchEntry? FindByKey(string key) => _byKey.GetValueOrDefault(key);
}
