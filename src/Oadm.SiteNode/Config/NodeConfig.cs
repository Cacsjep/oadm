using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Config;

/// <summary>
/// node.json in the data folder (docs/sites.md, "Data"): the node's choices and its bound server. No device data, no
/// passwords, ever. The TLS key is in its own encrypted file (<see cref="Identity.NodeIdentity"/>).
/// </summary>
public sealed class NodeConfig
{
    /// <summary>Hours of run time binding stays open after install, Reset or Allow binding.</summary>
    public static readonly TimeSpan BindingWindow = TimeSpan.FromHours(24);

    /// <summary>The default port of the server's site listener.</summary>
    public const int DefaultPort = 5081;

    public string Id { get; set; } = "";

    /// <summary>As typed: "oadm.example.com", "10.0.0.5", "10.0.0.5:5082".</summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>
    /// The chosen cards (true) while <see cref="AllCards"/> is off. No chosen card counts as all cards
    /// (<see cref="Networks.NetworkScope.ReachesAllCards"/>); false entries of older versions are ignored.
    /// </summary>
    public Dictionary<string, bool> Cards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reach devices on every card (default). Restricted to chosen cards when the machine also sits in an office network.</summary>
    public bool AllCards { get; set; } = true;

    /// <summary>Routed camera networks: "10.20.0.0/16".</summary>
    public List<string> ExtraSubnets { get; set; } = [];

    /// <summary>Both lists: Connect to (TCP 80, 443, 554 by default) and Listen on (empty by default).</summary>
    public List<PortEntry> Allowed { get; set; } = DefaultPorts();

    /// <summary>Ports the server asked for, until allowed or dismissed.</summary>
    public List<PortEntry> Requested { get; set; } = [];

    /// <summary>Run time left for binding, in seconds (counted by the service, saved every minute).</summary>
    public double BindingSecondsLeft { get; set; } = BindingWindow.TotalSeconds;

    /// <summary>At most one entry in v1.</summary>
    public List<BoundServer> BoundServers { get; set; } = [];

    /// <summary>The server's own names and addresses (SetServerAddresses), tried after the typed address.</summary>
    public ServerAddressList ServerAddresses { get; set; } = new();

    /// <summary>The bound server presented another identity (fingerprint hex); empty when not.</summary>
    public string NewServerIdentity { get; set; } = "";

    /// <summary>Where <see cref="NewServerIdentity"/> came from ("10.0.0.5:5081").</summary>
    public string NewServerFrom { get; set; } = "";

    /// <summary>When <see cref="NewServerIdentity"/> was first seen.</summary>
    public DateTimeOffset? NewServerSince { get; set; }

    /// <summary>
    /// Accepted in the app: that server may bind with the pairing code (the binding stays with the old identity until then).
    /// </summary>
    public bool NewServerAccepted { get; set; }

    public static List<PortEntry> DefaultPorts() =>
    [
        new(PortUse.Connect, PortProtocol.Tcp, 80),
        new(PortUse.Connect, PortProtocol.Tcp, 443),
        new(PortUse.Connect, PortProtocol.Tcp, 554),
    ];
}

/// <summary>The server the node is bound to.</summary>
public sealed class BoundServer
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the server's site identity certificate, upper case hex.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTimeOffset BoundUtc { get; set; }

    public string SiteName { get; set; } = "";
}

/// <summary>What SetServerAddresses sent.</summary>
public sealed class ServerAddressList
{
    public string DnsName { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public int Port { get; set; } = NodeConfig.DefaultPort;
}
