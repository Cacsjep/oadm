namespace Oadm.Core.Sites;

/// <summary>
/// How the server and a site node find each other (docs/spec/sites.md, The node: Outbound only): the node always connects to the
/// server's site port. The column Sites.Mode stays for the migrations (never deleted); nothing else is stored in it.
/// </summary>
public enum SiteMode
{
    /// <summary>The node connects to the server's site port (the only mode).</summary>
    Outbound = 0,

    /// <summary>The former mode where the server connected to the node; kept only so stored rows still read. Treated as <see cref="Outbound"/>.</summary>
    Inbound = 1,
}

/// <summary>
/// A bound site (row of Sites): a remote network the server reaches through one OADM Site Node. Online or offline lives in
/// memory only; the devices keep their last real status (Site offline is computed).
/// </summary>
public sealed class Site
{
    /// <summary>Most sites one server keeps.</summary>
    public const int MaxSites = 100;

    /// <summary>Longest site name.</summary>
    public const int MaxNameLength = 64;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>1..64 characters, trimmed, unique case-insensitive.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Upper invariant of <see cref="Name"/>: the unique index.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Always <see cref="SiteMode.Outbound"/> (the column stays for the migrations).</summary>
    public SiteMode Mode { get; set; }

    /// <summary>Where the node last connected from.</summary>
    public string? Address { get; set; }

    /// <summary>Not used (the column stays for the migrations): the node connects from a port of its own choice.</summary>
    public int? Port { get; set; }

    /// <summary>Hex SHA-256 of the node's certificate, pinned at bind.</summary>
    public string NodeKeyFingerprint { get; set; } = string.Empty;

    /// <summary>The node's random id (new at every Reset in the node app).</summary>
    public string NodeId { get; set; } = string.Empty;

    public DateTime BoundUtc { get; set; }

    /// <summary>Written at most once a minute.</summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>The node program's version from its last Hello.</summary>
    public string? Version { get; set; }

    /// <summary>The node forgot the server (Reset): its devices are Site offline until the site is replaced or removed.</summary>
    public bool NotBound { get; set; }

    public override string ToString() => $"Site {Name} ({Id})";
}

/// <summary>
/// A site removed with Keep devices (row of RemovedSites): its name for the devices that still carry its id, so the Sites
/// page lists it ("Removed") with Replace with and Remove devices until no device is left. Not a site: no node, never
/// online; its own table so a new site may take the name and a reset node may bind again.
/// </summary>
public sealed class RemovedSite
{
    /// <summary>The removed site's id (the devices' SiteId).</summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime RemovedUtc { get; set; }

    public override string ToString() => $"RemovedSite {Name} ({Id})";
}

/// <summary>Server settings of the sites (docs/spec/sites.md, Server side).</summary>
public static class SiteSettings
{
    /// <summary>The server's site identity (certificate and key, the key encrypted with the master key).</summary>
    public const string Identity = "Sites.Identity";

    /// <summary>Accept site nodes (bool, off by default).</summary>
    public const string Accept = "Sites.Accept";

    /// <summary>Where the site listener listens (default <see cref="DefaultListenUrl"/>).</summary>
    public const string ListenUrl = "Sites.ListenUrl";

    /// <summary>The site port: 5081 on every address.</summary>
    public const string DefaultListenUrl = "https://0.0.0.0:5081";

    /// <summary>The purpose of the identity key's encryption.</summary>
    public const string IdentityPurpose = "oadm:site-identity";
}
